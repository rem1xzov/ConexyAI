/**
 * MARKDOWN_M23: добавлено 2026-09-24 — парсер Markdown для ответов модели, без зависимостей.
 *
 * Старый рендер разбирал текст построчно регулярками, отсюда баги из ревью (M23): блок кода
 * закрывался на любой строке с ``` (даже на ```` ```` ```` или ~~~), нумерация списка всегда
 * начиналась с 1, вложенные списки схлопывались, а `2 * 3 * 4` превращалось в курсив.
 *
 * Здесь — небольшое подмножество CommonMark + GFM, достаточное для чата:
 *   блоки: абзацы, заголовки ATX, огороженный код (``` и ~~~ с правилом длины), списки
 *          (вложенные, `start` у нумерованных, tight/loose, чекбоксы GFM), цитаты, `---`,
 *          таблицы GFM, формулы `$$…$$` / `\[…\]` и ```math;
 *   строки: `код`, $формулы$ / \(…\) / $$…$$, ссылки (только http/https/mailto), автоссылки,
 *          *em* / **strong** / ~~del~~ по правилам flanking-разделителей CommonMark,
 *          экранирование `\*`, жёсткий перенос (два пробела / `\` в конце строки).
 * Сырой HTML не поддерживается: он остаётся текстом, а React экранирует его при выводе.
 *
 * Модуль чистый (без React), чтобы разбор можно было проверять отдельно от рендера.
 */

export type TableAlign = 'left' | 'center' | 'right';

export interface ListItem {
  blocks: Block[];
  /** GFM task item: `- [ ]` (false) / `- [x]` (true); undefined for an ordinary item. */
  checked?: boolean;
}

export type Block =
  | { type: 'paragraph'; text: string }
  | { type: 'heading'; level: number; text: string }
  | { type: 'code'; lang: string; code: string; closed: boolean }
  | { type: 'list'; ordered: boolean; start: number; tight: boolean; items: ListItem[] }
  | { type: 'blockquote'; children: Block[] }
  | { type: 'hr' }
  | { type: 'table'; header: string[]; aligns: TableAlign[]; rows: string[][] }
  | { type: 'math'; tex: string };

export type Inline =
  | { type: 'text'; value: string }
  | { type: 'code'; value: string }
  | { type: 'math'; value: string; display: boolean }
  | { type: 'link'; href: string; children: Inline[] }
  | { type: 'em' | 'strong' | 'del'; children: Inline[] }
  | { type: 'br' };

// ---------------------------------------------------------------------------------------------
// Block level
// ---------------------------------------------------------------------------------------------

/** Nested lists / quotes deeper than this are flattened into text (guards against stack abuse). */
const MAX_DEPTH = 12;

interface Fence {
  indent: number;
  char: '`' | '~';
  length: number;
  info: string;
}

/** Leading tabs count as 4 columns, the way editors and CommonMark treat indentation. */
function expandIndent(line: string): string {
  if (!line.startsWith('\t') && !/^ +\t/.test(line)) return line;
  const m = /^[ \t]+/.exec(line);
  if (!m) return line;
  let width = 0;
  for (const ch of m[0]) width = ch === '\t' ? width + 4 - (width % 4) : width + 1;
  return ' '.repeat(width) + line.slice(m[0].length);
}

function indentOf(line: string): number {
  let n = 0;
  while (n < line.length && line[n] === ' ') n++;
  return n;
}

function isBlank(line: string): boolean {
  return line.trim() === '';
}

/** Removes up to `n` leading spaces. */
function dedent(line: string, n: number): string {
  const strip = Math.min(n, indentOf(line));
  return line.slice(strip);
}

export function matchFence(line: string): Fence | null {
  const m = /^( *)(`{3,}|~{3,})(.*)$/.exec(line);
  if (!m) return null;
  const char = m[2][0] as '`' | '~';
  const info = m[3].trim();
  // A backtick fence's info string may not contain backticks (otherwise ```a``` is inline code).
  if (char === '`' && info.includes('`')) return null;
  return { indent: m[1].length, char, length: m[2].length, info };
}

/** CommonMark: the closing fence uses the same character and is at least as long as the opener. */
export function isFenceClose(line: string, fence: Fence): boolean {
  const m = /^ *(`{3,}|~{3,}) *$/.exec(line);
  return Boolean(m && m[1][0] === fence.char && m[1].length >= fence.length);
}

const HEADING_RE = /^ {0,3}(#{1,6})(?:[ \t]+(.*?))?[ \t]*$/;
const HR_RE = /^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$/;
const QUOTE_RE = /^ {0,3}> ?(.*)$/;
const LIST_RE = /^( *)([-*+]|\d{1,9}[.)])(?:([ \t]+)(.*))?$/;

interface ListMarker {
  indent: number;
  ordered: boolean;
  /** Bullet character or the ordered delimiter (`.` / `)`). */
  marker: string;
  start: number;
  /** Column where the item's content starts. */
  contentIndent: number;
  content: string;
}

function matchListItem(line: string): ListMarker | null {
  const m = LIST_RE.exec(line);
  if (!m) return null;
  if (HR_RE.test(line)) return null;
  const indent = m[1].length;
  const rawMarker = m[2];
  const ordered = /\d/.test(rawMarker[0]);
  const spaces = m[3] ? m[3].replace(/\t/g, '    ').length : 0;
  const content = m[4] ?? '';
  // More than 4 spaces after the marker: the content is indented code in CommonMark; treat the
  // extra spaces as part of the content and keep the item's content column right after one space.
  const gap = content === '' || spaces > 4 ? 1 : spaces;
  return {
    indent,
    ordered,
    marker: ordered ? rawMarker[rawMarker.length - 1] : rawMarker,
    start: ordered ? parseInt(rawMarker, 10) : 1,
    contentIndent: indent + rawMarker.length + gap,
    content: spaces > 4 ? ' '.repeat(spaces - 1) + content : content,
  };
}

function isMathBlockStart(line: string): boolean {
  const t = line.trim();
  return t.startsWith('$$') || t.startsWith('\\[');
}

/** Splits one GFM row on unescaped pipes outside code spans, dropping the outer pipes. */
export function splitTableRow(line: string): string[] {
  let text = line.trim();
  if (text.startsWith('|')) text = text.slice(1);
  if (text.endsWith('|') && !text.endsWith('\\|')) text = text.slice(0, -1);
  const cells: string[] = [];
  let current = '';
  let tick = 0;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (ch === '\\' && text[i + 1] === '|') {
      current += '\\|';
      i++;
      continue;
    }
    if (ch === '`') {
      let run = 1;
      while (text[i + run] === '`') run++;
      if (tick === 0) tick = run;
      else if (tick === run) tick = 0;
      current += text.slice(i, i + run);
      i += run - 1;
      continue;
    }
    if (ch === '|' && tick === 0) {
      cells.push(current.trim());
      current = '';
      continue;
    }
    current += ch;
  }
  cells.push(current.trim());
  return cells;
}

function tableAlign(cell: string): TableAlign {
  const left = cell.startsWith(':');
  const right = cell.endsWith(':');
  if (left && right) return 'center';
  return right ? 'right' : 'left';
}

/** TABLES_GFM: a table only starts when a delimiter row follows the header directly. */
export function isTableDelimiter(line: string | undefined): boolean {
  if (line === undefined) return false;
  const trimmed = line.trim();
  if (trimmed === '' || !trimmed.includes('|') || !trimmed.includes('-')) return false;
  const cells = splitTableRow(trimmed);
  return cells.length > 0 && cells.every((cell) => /^:?-+:?$/.test(cell));
}

function isTableStart(lines: string[], i: number): boolean {
  return lines[i].includes('|') && isTableDelimiter(lines[i + 1]);
}

/** True when `line` opens a block that interrupts a running paragraph. */
function startsBlock(lines: string[], i: number): boolean {
  const line = lines[i];
  if (matchFence(line) || HEADING_RE.test(line) || HR_RE.test(line) || QUOTE_RE.test(line)) return true;
  if (isMathBlockStart(line)) return true;
  const item = matchListItem(line);
  if (item && item.content.trim() !== '') return true;
  return isTableStart(lines, i);
}

/**
 * Reads a `$$ … $$` / `\[ … \]` block; returns null when it is not a well-formed block (then the
 * text stays an ordinary paragraph). Display math cannot contain a blank line, so an unclosed `$$`
 * never swallows the rest of the message — it stops at the first blank line.
 */
function readMathBlock(lines: string[], start: number): { tex: string; next: number } | null {
  const first = lines[start].trim();
  const close = first.startsWith('$$') ? '$$' : '\\]';
  const rest = first.slice(2).trim();
  if (rest.endsWith(close) && rest.length > close.length) {
    // Single line: `$$ x^2 $$`. A `$$` in the middle means inline display math inside a sentence.
    const body = rest.slice(0, -close.length);
    return body.includes(close) || !body.trim() ? null : { tex: body.trim(), next: start + 1 };
  }
  if (rest.includes(close)) return null;
  const body: string[] = rest ? [rest] : [];
  for (let i = start + 1; i < lines.length; i++) {
    if (isBlank(lines[i])) return null;
    const t = lines[i].trimEnd();
    if (t.endsWith(close)) {
      body.push(t.slice(0, -close.length));
      const tex = body.join('\n').trim();
      return tex ? { tex, next: i + 1 } : null;
    }
    body.push(lines[i]);
  }
  // Unclosed (usually still streaming): leave the text as ordinary paragraphs.
  return null;
}

function parseList(lines: string[], start: number, depth: number): { block: Block; next: number } {
  const first = matchListItem(lines[start])!;
  const items: ListItem[] = [];
  let tight = true;
  let i = start;

  while (i < lines.length) {
    const m = matchListItem(lines[i]);
    if (!m || m.ordered !== first.ordered || m.marker !== first.marker || m.indent > first.indent + 3) break;

    const itemLines: string[] = [m.content];
    let fence: Fence | null = matchFence(m.content);
    let sawBlank = false;
    let innerBlank = false;
    let j = i + 1;

    while (j < lines.length) {
      const line = lines[j];
      if (fence) {
        // Everything up to the closing fence belongs to the item, even when the model did not
        // indent the code under the bullet.
        const body = dedent(line, m.contentIndent);
        itemLines.push(body);
        if (isFenceClose(body, fence) || isFenceClose(line.trimStart(), fence)) fence = null;
        j++;
        continue;
      }
      if (isBlank(line)) {
        sawBlank = true;
        itemLines.push('');
        j++;
        continue;
      }
      const indent = indentOf(line);
      if (indent >= m.contentIndent) {
        if (sawBlank) innerBlank = true;
        sawBlank = false;
        const body = dedent(line, m.contentIndent);
        fence = matchFence(body);
        itemLines.push(body);
        j++;
        continue;
      }
      const nested = matchListItem(line);
      const looseFence = indent > m.indent ? matchFence(line.trimStart()) : null;
      if (looseFence) {
        // Code under a bullet indented by fewer columns than the text ("1. Step\n  ```bash").
        if (sawBlank) innerBlank = true;
        sawBlank = false;
        fence = looseFence;
        itemLines.push(dedent(line, indent));
        j++;
        continue;
      }
      if (nested && nested.indent > m.indent) {
        // A sub-list indented less than the content column ("1. a\n  - b"): still nested.
        if (sawBlank) innerBlank = true;
        sawBlank = false;
        itemLines.push(dedent(line, indent));
        j++;
        continue;
      }
      if (!sawBlank && !nested && !startsBlock(lines, j)) {
        // Lazy continuation of the item's paragraph.
        itemLines.push(line.trim());
        j++;
        continue;
      }
      break;
    }

    while (itemLines.length > 0 && isBlank(itemLines[itemLines.length - 1])) itemLines.pop();

    let checked: boolean | undefined;
    const task = /^\[([ xX])\][ \t]+/.exec(itemLines[0] ?? '');
    if (task) {
      checked = task[1] !== ' ';
      itemLines[0] = itemLines[0].slice(task[0].length);
    }

    items.push({ blocks: parseBlockLines(itemLines, depth + 1), checked });
    if (innerBlank) tight = false;
    i = j;

    if (sawBlank) {
      const sibling = i < lines.length ? matchListItem(lines[i]) : null;
      if (
        sibling &&
        sibling.ordered === first.ordered &&
        sibling.marker === first.marker &&
        sibling.indent <= first.indent + 3
      ) {
        tight = false;
        continue;
      }
      break;
    }
  }

  return {
    block: { type: 'list', ordered: first.ordered, start: first.start, tight, items },
    next: i,
  };
}

function parseBlockLines(rawLines: string[], depth: number): Block[] {
  const lines = rawLines.map(expandIndent);
  const blocks: Block[] = [];
  let paragraph: string[] = [];

  const flushParagraph = () => {
    if (paragraph.length === 0) return;
    blocks.push({ type: 'paragraph', text: paragraph.join('\n') });
    paragraph = [];
  };

  if (depth > MAX_DEPTH) {
    const text = lines.join('\n').trim();
    return text ? [{ type: 'paragraph', text }] : [];
  }

  let i = 0;
  while (i < lines.length) {
    const line = lines[i];

    if (isBlank(line)) {
      flushParagraph();
      i++;
      continue;
    }

    const fence = matchFence(line);
    if (fence) {
      flushParagraph();
      const code: string[] = [];
      let closed = false;
      let j = i + 1;
      for (; j < lines.length; j++) {
        if (isFenceClose(lines[j], fence)) {
          closed = true;
          j++;
          break;
        }
        code.push(dedent(lines[j], fence.indent));
      }
      const lang = fence.info.split(/\s+/)[0] ?? '';
      if (lang.toLowerCase() === 'math' && closed && code.join('').trim()) {
        blocks.push({ type: 'math', tex: code.join('\n').trim() });
      } else {
        blocks.push({ type: 'code', lang, code: code.join('\n'), closed });
      }
      i = j;
      continue;
    }

    if (isMathBlockStart(line)) {
      const math = readMathBlock(lines, i);
      if (math) {
        flushParagraph();
        blocks.push({ type: 'math', tex: math.tex });
        i = math.next;
        continue;
      }
    }

    const heading = HEADING_RE.exec(line);
    if (heading) {
      flushParagraph();
      const text = (heading[2] ?? '').replace(/[ \t]+#+[ \t]*$/, '').replace(/^#+$/, '');
      blocks.push({ type: 'heading', level: heading[1].length, text });
      i++;
      continue;
    }

    if (HR_RE.test(line)) {
      flushParagraph();
      blocks.push({ type: 'hr' });
      i++;
      continue;
    }

    if (QUOTE_RE.test(line)) {
      flushParagraph();
      const quoted: string[] = [];
      let j = i;
      let lastWasText = false;
      while (j < lines.length) {
        const q = QUOTE_RE.exec(lines[j]);
        if (q) {
          quoted.push(q[1]);
          lastWasText = !isBlank(q[1]);
          j++;
          continue;
        }
        // Lazy continuation: a plain line right after quoted text still belongs to the quote.
        if (lastWasText && !isBlank(lines[j]) && !startsBlock(lines, j)) {
          quoted.push(lines[j].trim());
          j++;
          continue;
        }
        break;
      }
      blocks.push({ type: 'blockquote', children: parseBlockLines(quoted, depth + 1) });
      i = j;
      continue;
    }

    const item = matchListItem(line);
    // An empty bullet cannot interrupt a paragraph (so a lone "-" line stays text).
    if (item && (paragraph.length === 0 || item.content.trim() !== '')) {
      flushParagraph();
      const { block, next } = parseList(lines, i, depth);
      blocks.push(block);
      i = next;
      continue;
    }

    if (isTableStart(lines, i)) {
      flushParagraph();
      const header = splitTableRow(lines[i]);
      const aligns = splitTableRow(lines[i + 1]).map(tableAlign);
      const rows: string[][] = [];
      let j = i + 2;
      while (j < lines.length && !isBlank(lines[j]) && lines[j].includes('|')) {
        rows.push(splitTableRow(lines[j]));
        j++;
      }
      blocks.push({ type: 'table', header, aligns, rows });
      i = j;
      continue;
    }

    paragraph.push(line.trim() === line ? line : line.replace(/^\s+/, ''));
    i++;
  }
  flushParagraph();
  return blocks;
}

/** Parses Markdown text into blocks. */
export function parseMarkdown(text: string): Block[] {
  return parseBlockLines(text.replace(/\r\n?/g, '\n').split('\n'), 0);
}

// ---------------------------------------------------------------------------------------------
// Inline level
// ---------------------------------------------------------------------------------------------

interface Delim {
  type: 'delim';
  char: '*' | '_' | '~';
  count: number;
  origCount: number;
  canOpen: boolean;
  canClose: boolean;
}

type Item = Inline | Delim;

/** Emphasis matching is quadratic in the worst case; beyond this many runs it is skipped. */
const MAX_DELIMS = 400;

const ASCII_PUNCT = /[!-/:-@[-`{-~]/;
let unicodePunct: RegExp | null = null;

function isPunct(ch: string): boolean {
  if (ASCII_PUNCT.test(ch)) return true;
  try {
    unicodePunct ??= new RegExp('[\\p{P}\\p{S}]', 'u');
    return unicodePunct.test(ch);
  } catch {
    return false;
  }
}

function isWhitespace(ch: string): boolean {
  return ch === '' || /\s/.test(ch);
}

function isSafeHref(href: string): boolean {
  return /^(https?:\/\/|mailto:)/i.test(href);
}

/** Finds the closing `$` of inline math starting at `open` (Pandoc rules), or -1. */
function findInlineMathClose(text: string, open: number): number {
  const first = text[open + 1];
  if (first === undefined || isWhitespace(first) || first === '$') return -1;
  for (let j = open + 1; j < text.length; j++) {
    const ch = text[j];
    if (ch === '\\') {
      j++;
      continue;
    }
    if (ch === '\n' && text[j + 1] === '\n') return -1;
    if (ch !== '$') continue;
    const before = text[j - 1];
    const after = text[j + 1] ?? '';
    // `$5 and $10`: a closing `$` may not follow a space nor precede a digit.
    if (isWhitespace(before) || /\d/.test(after)) {
      // A `$` that cannot close but could open a formula of its own means the first one was a
      // currency sign ("$5 and $10. Formula: $E=mc^2$") — give up instead of swallowing the text.
      if (after && !isWhitespace(after)) return -1;
      continue;
    }
    return j;
  }
  return -1;
}

/** Index right after the `]` that closes the bracket opened at `open`, or -1. */
function findLabelEnd(text: string, open: number): number {
  let depth = 0;
  for (let j = open; j < text.length; j++) {
    const ch = text[j];
    if (ch === '\\') {
      j++;
      continue;
    }
    if (ch === '`') {
      let run = 1;
      while (text[j + run] === '`') run++;
      const close = text.indexOf('`'.repeat(run), j + run);
      if (close > 0) j = close + run - 1;
      continue;
    }
    if (ch === '[') depth++;
    else if (ch === ']') {
      depth--;
      if (depth === 0) return j + 1;
    }
  }
  return -1;
}

/** Parses `(url "title")` right after a link label; returns the href and the end index. */
function readLinkDestination(text: string, at: number): { href: string; end: number } | null {
  if (text[at] !== '(') return null;
  const m = /^\(\s*<?([^\s<>()]*(?:\([^\s()]*\)[^\s<>()]*)*)>?(?:\s+(?:"[^"]*"|'[^']*'))?\s*\)/.exec(text.slice(at));
  if (!m) return null;
  return { href: m[1], end: at + m[0].length };
}

const URL_RE = /^https?:\/\/[^\s<>"'`]+/i;

/** Bare URL autolink, trimmed of trailing punctuation and unbalanced closing brackets. */
function readBareUrl(text: string, at: number): string | null {
  const m = URL_RE.exec(text.slice(at));
  if (!m) return null;
  let url = m[0];
  for (;;) {
    const last = url[url.length - 1];
    if (/[.,;:!?*_~]/.test(last)) {
      url = url.slice(0, -1);
      continue;
    }
    if (last === ')' && (url.match(/\(/g)?.length ?? 0) < (url.match(/\)/g)?.length ?? 0)) {
      url = url.slice(0, -1);
      continue;
    }
    break;
  }
  return url.length > 'https://'.length ? url : null;
}

function pushText(items: Item[], value: string): void {
  if (!value) return;
  const last = items[items.length - 1];
  if (last && last.type === 'text') last.value += value;
  else items.push({ type: 'text', value });
}

function scanInline(text: string, depth: number): Item[] {
  const items: Item[] = [];
  let i = 0;
  let buf = '';
  const flush = () => {
    pushText(items, buf);
    buf = '';
  };

  while (i < text.length) {
    const ch = text[i];

    // Escapes, `\(`…`\)` / `\[`…`\]` math, backslash hard break.
    if (ch === '\\') {
      const next = text[i + 1];
      if (next === '(' || next === '[') {
        const close = text.indexOf(next === '(' ? '\\)' : '\\]', i + 2);
        if (close > i + 2) {
          flush();
          items.push({ type: 'math', value: text.slice(i + 2, close).trim(), display: next === '[' });
          i = close + 2;
          continue;
        }
      }
      if (next === '\n') {
        flush();
        items.push({ type: 'br' });
        i += 2;
        continue;
      }
      if (next !== undefined && ASCII_PUNCT.test(next)) {
        buf += next;
        i += 2;
        continue;
      }
      buf += ch;
      i++;
      continue;
    }

    // Code spans win over everything else: nothing inside them is parsed.
    if (ch === '`') {
      let run = 1;
      while (text[i + run] === '`') run++;
      const fence = '`'.repeat(run);
      let search = i + run;
      let close = -1;
      while (search < text.length) {
        const found = text.indexOf(fence, search);
        if (found < 0) break;
        let end = found + run;
        if (text[end] !== '`') {
          close = found;
          break;
        }
        while (text[end] === '`') end++;
        search = end;
      }
      if (close < 0) {
        buf += fence;
        i += run;
        continue;
      }
      flush();
      let value = text.slice(i + run, close).replace(/\n/g, ' ');
      if (value.length >= 3 && value.startsWith(' ') && value.endsWith(' ') && value.trim()) value = value.slice(1, -1);
      items.push({ type: 'code', value });
      i = close + run;
      continue;
    }

    if (ch === '$') {
      if (text[i + 1] === '$') {
        const close = text.indexOf('$$', i + 2);
        if (close > i + 2 && text.slice(i + 2, close).trim()) {
          flush();
          items.push({ type: 'math', value: text.slice(i + 2, close).trim(), display: true });
          i = close + 2;
          continue;
        }
        buf += '$$';
        i += 2;
        continue;
      }
      const close = findInlineMathClose(text, i);
      if (close > 0) {
        flush();
        items.push({ type: 'math', value: text.slice(i + 1, close), display: false });
        i = close + 1;
        continue;
      }
      buf += ch;
      i++;
      continue;
    }

    if (ch === '[' || (ch === '!' && text[i + 1] === '[')) {
      const labelStart = ch === '!' ? i + 1 : i;
      const labelEnd = findLabelEnd(text, labelStart);
      const dest = labelEnd > 0 ? readLinkDestination(text, labelEnd) : null;
      if (dest) {
        flush();
        const label = text.slice(labelStart + 1, labelEnd - 1);
        const children =
          depth < 4 ? parseInlineDepth(label, depth + 1) : [{ type: 'text' as const, value: label }];
        if (isSafeHref(dest.href)) {
          items.push({
            type: 'link',
            href: dest.href,
            children: children.length ? children : [{ type: 'text', value: dest.href }],
          });
        } else {
          // Only real web links become anchors; `javascript:` and relative paths stay text.
          for (const c of children) items.push(c);
        }
        i = dest.end;
        continue;
      }
      buf += ch;
      i++;
      continue;
    }

    if (ch === '<') {
      const auto = /^<((?:https?:\/\/|mailto:)[^\s<>]+)>/i.exec(text.slice(i));
      if (auto) {
        flush();
        items.push({ type: 'link', href: auto[1], children: [{ type: 'text', value: auto[1] }] });
        i += auto[0].length;
        continue;
      }
      buf += ch;
      i++;
      continue;
    }

    if ((ch === 'h' || ch === 'H') && !/[\w/]/.test(text[i - 1] ?? '')) {
      const url = readBareUrl(text, i);
      if (url) {
        flush();
        items.push({ type: 'link', href: url, children: [{ type: 'text', value: url }] });
        i += url.length;
        continue;
      }
    }

    if (ch === '*' || ch === '_' || ch === '~') {
      let run = 1;
      while (text[i + run] === ch) run++;
      // `~` strikes through only as `~~`; a lone `~5 min` is plain text.
      if (ch === '~' && run !== 2) {
        buf += text.slice(i, i + run);
        i += run;
        continue;
      }
      const before = i > 0 ? text[i - 1] : '';
      const after = text[i + run] ?? '';
      const leftFlanking =
        !isWhitespace(after) && (!isPunct(after) || isWhitespace(before) || isPunct(before));
      const rightFlanking =
        !isWhitespace(before) && (!isPunct(before) || isWhitespace(after) || isPunct(after));
      let canOpen = leftFlanking;
      let canClose = rightFlanking;
      if (ch === '_') {
        // Intraword `_` (snake_case) never emphasises.
        canOpen = leftFlanking && (!rightFlanking || isPunct(before));
        canClose = rightFlanking && (!leftFlanking || isPunct(after));
      }
      flush();
      if (canOpen || canClose) {
        items.push({ type: 'delim', char: ch, count: run, origCount: run, canOpen, canClose });
      } else {
        pushText(items, text.slice(i, i + run));
      }
      i += run;
      continue;
    }

    if (ch === '\n') {
      // Two trailing spaces before a newline = hard line break.
      if (/ {2,}$/.test(buf)) {
        buf = buf.replace(/ +$/, '');
        flush();
        items.push({ type: 'br' });
      } else {
        buf = buf.replace(/ +$/, '');
        buf += '\n';
      }
      i++;
      continue;
    }

    buf += ch;
    i++;
  }
  flush();
  return items;
}

function delimText(d: Delim): Inline {
  return { type: 'text', value: d.char.repeat(d.count) };
}

function finalize(items: Item[]): Inline[] {
  const out: Inline[] = [];
  for (const item of items) {
    const node = item.type === 'delim' ? delimText(item) : item;
    const last = out[out.length - 1];
    if (node.type === 'text' && last && last.type === 'text') last.value += node.value;
    else out.push(node.type === 'text' ? { ...node } : node);
  }
  return out;
}

/** CommonMark "process emphasis": pairs delimiter runs into em / strong / del nodes. */
function processEmphasis(items: Item[]): void {
  let delimCount = 0;
  for (const it of items) if (it.type === 'delim') delimCount++;
  if (delimCount < 2 || delimCount > MAX_DELIMS) return;

  for (let c = 0; c < items.length; c++) {
    const closer = items[c];
    if (closer.type !== 'delim' || !closer.canClose || closer.count === 0) continue;

    let found = -1;
    for (let o = c - 1; o >= 0; o--) {
      const opener = items[o];
      if (opener.type !== 'delim' || opener.char !== closer.char || !opener.canOpen || opener.count === 0) continue;
      if (closer.char === '~') {
        if (opener.count !== closer.count) continue;
      } else if (
        (opener.canClose || closer.canOpen) &&
        (opener.origCount + closer.origCount) % 3 === 0 &&
        !(opener.origCount % 3 === 0 && closer.origCount % 3 === 0)
      ) {
        // The "rule of 3" keeps `*foo**bar*` from pairing the wrong runs.
        continue;
      }
      found = o;
      break;
    }
    if (found < 0) continue;

    const opener = items[found] as Delim;
    const use = closer.char === '~' ? closer.count : opener.count >= 2 && closer.count >= 2 ? 2 : 1;
    const type = closer.char === '~' ? 'del' : use === 2 ? 'strong' : 'em';
    const node: Inline = { type, children: finalize(items.slice(found + 1, c)) };
    opener.count -= use;
    closer.count -= use;

    const replacement: Item[] = [];
    if (opener.count > 0) replacement.push(opener);
    replacement.push(node);
    if (closer.count > 0) replacement.push(closer);
    items.splice(found, c - found + 1, ...replacement);

    // Re-examine the closer when it still has characters left, otherwise move past the new node.
    c = found + replacement.length - 1 - (closer.count > 0 ? 1 : 0);
  }
}

function parseInlineDepth(text: string, depth: number): Inline[] {
  const items = scanInline(text, depth);
  processEmphasis(items);
  return finalize(items);
}

/** Parses the inline content of one paragraph / heading / cell. */
export function parseInline(text: string): Inline[] {
  return parseInlineDepth(text, 0);
}

/** Plain text of inline nodes (used for the short-term badge heuristic). */
export function inlineText(nodes: Inline[]): string {
  let out = '';
  for (const n of nodes) {
    if (n.type === 'text' || n.type === 'code' || n.type === 'math') out += n.value;
    else if (n.type === 'br') out += '\n';
    else out += inlineText(n.children);
  }
  return out;
}
