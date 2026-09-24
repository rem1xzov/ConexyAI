/**
 * ARTIFACTS: добавлено 2026-09-24 — разбор тега `<conexy_artifact>` из ответа модели (контракт C-9).
 *
 * Модель оборачивает самостоятельный контент (HTML-страницу, SVG, диаграмму, документ, код) в
 *   <conexy_artifact identifier="…" type="…" title="…" language="…">…</conexy_artifact>
 * Тег режется из текста ещё до Markdown: на его месте в сообщении рисуется карточка, а само
 * содержимое открывается в правой панели. Тег может прийти не целиком (идёт стрим) — тогда
 * возвращается незавершённый артефакт, и карточка показывает «создаю артефакт…».
 *
 * Теги внутри огороженных блоков кода и inline-кода не считаются артефактами: модель может
 * объяснять сам формат, и такой пример должен остаться текстом.
 */

import { extensionForLanguage } from './highlight';

export type ArtifactKind = 'text/html' | 'image/svg+xml' | 'text/mermaid' | 'text/markdown' | 'application/code';

export interface ParsedArtifact {
  identifier: string;
  type: ArtifactKind;
  title: string;
  language?: string;
  content: string;
  /** False while the closing tag has not arrived yet. */
  complete: boolean;
  /** True while even the opening tag is incomplete: attributes (identifier!) may still change. */
  pendingTag?: boolean;
}

export type ContentSegment =
  | { kind: 'markdown'; text: string }
  | { kind: 'artifact'; artifact: ParsedArtifact; index: number };

const OPEN_TAG = '<conexy_artifact';
const CLOSE_TAG = '</conexy_artifact>';

const TYPE_ALIASES: Record<string, ArtifactKind> = {
  'text/html': 'text/html',
  html: 'text/html',
  'application/vnd.ant.html': 'text/html',
  'image/svg+xml': 'image/svg+xml',
  svg: 'image/svg+xml',
  'image/svg': 'image/svg+xml',
  'text/mermaid': 'text/mermaid',
  mermaid: 'text/mermaid',
  'application/vnd.ant.mermaid': 'text/mermaid',
  'text/markdown': 'text/markdown',
  markdown: 'text/markdown',
  md: 'text/markdown',
  'text/plain': 'text/markdown',
  'application/code': 'application/code',
  code: 'application/code',
  'application/vnd.ant.code': 'application/code',
};

/** Types that have a rendered preview (everything except plain code). */
export function hasPreview(type: ArtifactKind): boolean {
  return type !== 'application/code';
}

function decodeEntities(value: string): string {
  return value
    .replace(/&quot;/g, '"')
    .replace(/&#39;|&apos;/g, "'")
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&amp;/g, '&');
}

function parseAttributes(source: string): Record<string, string> {
  const attrs: Record<string, string> = {};
  const re = /([A-Za-z_][\w:.-]*)\s*=\s*(?:"([^"]*)"|'([^']*)')/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(source)) !== null) attrs[m[1].toLowerCase()] = decodeEntities(m[2] ?? m[3] ?? '');
  return attrs;
}

function inferType(rawType: string | undefined, language: string | undefined, content: string): ArtifactKind {
  const normalized = (rawType ?? '').trim().toLowerCase();
  if (TYPE_ALIASES[normalized]) return TYPE_ALIASES[normalized];
  if (normalized.includes('react')) return 'application/code';
  const lang = (language ?? '').toLowerCase();
  if (lang === 'html') return 'text/html';
  if (lang === 'svg') return 'image/svg+xml';
  if (lang === 'mermaid') return 'text/mermaid';
  if (lang === 'markdown' || lang === 'md') return 'text/markdown';
  if (lang) return 'application/code';
  const head = content.trimStart().slice(0, 200).toLowerCase();
  if (head.startsWith('<svg') || (head.startsWith('<?xml') && head.includes('<svg'))) return 'image/svg+xml';
  if (head.startsWith('<!doctype html') || head.startsWith('<html')) return 'text/html';
  return 'text/markdown';
}

/** `React` type artifacts are shown as JSX code (there is no bundler in the preview). */
function inferLanguage(rawType: string | undefined, language: string | undefined): string | undefined {
  if (language && language.trim()) return language.trim();
  if ((rawType ?? '').toLowerCase().includes('react')) return 'jsx';
  return undefined;
}

/** Models sometimes wrap the body in ``` fences despite the instructions — unwrap it. */
function unwrapFence(body: string): string {
  const m = /^\s*(`{3,}|~{3,})[^\n]*\n([\s\S]*?)\n?\s*\1\s*$/.exec(body);
  return m ? m[2] : body;
}

/** Index of `needle` in `line` outside inline code spans (even backtick count before it). */
function indexOutsideCode(line: string, needle: string, from = 0): number {
  let idx = line.indexOf(needle, from);
  while (idx >= 0) {
    let ticks = 0;
    for (let i = 0; i < idx; i++) if (line[i] === '`') ticks++;
    if (ticks % 2 === 0) return idx;
    idx = line.indexOf(needle, idx + needle.length);
  }
  return -1;
}

/** End index (exclusive) of the opening tag starting at `start`, skipping `>` inside quotes; -1 if not there yet. */
function findOpenTagEnd(content: string, start: number): number {
  let quote: string | null = null;
  for (let i = start + OPEN_TAG.length; i < content.length; i++) {
    const ch = content[i];
    if (quote) {
      if (ch === quote) quote = null;
    } else if (ch === '"' || ch === "'") {
      quote = ch;
    } else if (ch === '>') {
      return i + 1;
    }
  }
  return -1;
}

/** Strips a trailing prefix of `tag` (e.g. `</conexy_arti`) that is still arriving. */
function stripPartialSuffix(text: string, tag: string): string {
  for (let len = Math.min(tag.length - 1, text.length); len > 0; len--) {
    if (text.endsWith(tag.slice(0, len))) return text.slice(0, text.length - len);
  }
  return text;
}

const FENCE_RE = /^ *(`{3,}|~{3,})/;

function buildArtifact(tagSource: string, body: string, complete: boolean, index: number): ParsedArtifact {
  const attrs = parseAttributes(tagSource);
  let content = body.replace(/^\r?\n/, '');
  if (complete) content = unwrapFence(content.replace(/\r?\n$/, ''));
  const language = inferLanguage(attrs.type, attrs.language);
  const type = inferType(attrs.type, language, content);
  const identifier = (attrs.identifier ?? attrs.id ?? '').trim() || `artifact-${index + 1}`;
  return {
    identifier,
    type,
    title: (attrs.title ?? '').trim(),
    language: type === 'application/code' ? language : language || undefined,
    content,
    complete,
  };
}

/**
 * Splits a message into Markdown text and artifacts. `streaming` enables hiding a tag that has
 * only partially arrived (`<conexy_art`), so raw markup never flashes on screen.
 */
export function splitArtifacts(content: string, streaming = false): ContentSegment[] {
  if (!content.includes(OPEN_TAG)) {
    const text = streaming ? stripPartialSuffix(content, OPEN_TAG) : content;
    return text ? [{ kind: 'markdown', text }] : [];
  }

  const segments: ContentSegment[] = [];
  let artifactIndex = 0;
  let segStart = 0;
  let pos = 0;
  let fence: { char: string; length: number } | null = null;

  const pushMarkdown = (end: number) => {
    const text = content.slice(segStart, end);
    if (text.trim()) segments.push({ kind: 'markdown', text });
  };

  while (pos < content.length) {
    const nl = content.indexOf('\n', pos);
    const lineEnd = nl < 0 ? content.length : nl;
    const line = content.slice(pos, lineEnd);

    if (fence) {
      const close = /^ *(`{3,}|~{3,}) *$/.exec(line);
      if (close && close[1][0] === fence.char && close[1].length >= fence.length) fence = null;
      pos = lineEnd + 1;
      continue;
    }
    const open = FENCE_RE.exec(line);
    if (open && !(open[1][0] === '`' && line.slice(open[0].length).includes('`'))) {
      fence = { char: open[1][0], length: open[1].length };
      pos = lineEnd + 1;
      continue;
    }

    const at = indexOutsideCode(line, OPEN_TAG);
    const after = line[at + OPEN_TAG.length];
    if (at < 0 || (after !== undefined && !/[\s>]/.test(after))) {
      pos = lineEnd + 1;
      continue;
    }

    const tagStart = pos + at;
    pushMarkdown(tagStart);
    const tagEnd = findOpenTagEnd(content, tagStart);
    if (tagEnd < 0) {
      // The opening tag itself is still streaming: show the card with what is known so far.
      segments.push({
        kind: 'artifact',
        artifact: { ...buildArtifact(content.slice(tagStart + OPEN_TAG.length), '', false, artifactIndex), pendingTag: true },
        index: artifactIndex,
      });
      return segments;
    }

    const tagSource = content.slice(tagStart + OPEN_TAG.length, tagEnd - 1);
    const close = content.indexOf(CLOSE_TAG, tagEnd);
    if (close < 0) {
      const body = stripPartialSuffix(content.slice(tagEnd), CLOSE_TAG);
      segments.push({
        kind: 'artifact',
        artifact: buildArtifact(tagSource, body, false, artifactIndex),
        index: artifactIndex,
      });
      return segments;
    }

    segments.push({
      kind: 'artifact',
      artifact: buildArtifact(tagSource, content.slice(tagEnd, close), true, artifactIndex),
      index: artifactIndex,
    });
    artifactIndex++;
    pos = close + CLOSE_TAG.length;
    segStart = pos;
  }

  const tail = content.slice(segStart);
  const text = streaming ? stripPartialSuffix(tail, OPEN_TAG) : tail;
  if (text.trim()) segments.push({ kind: 'markdown', text });
  return segments;
}

/**
 * The message text with every artifact turned into an ordinary fenced block — used for "copy"
 * and office export, where the raw `<conexy_artifact>` markup would be noise.
 */
export function artifactsToMarkdown(content: string): string {
  if (!content.includes(OPEN_TAG)) return content;
  return splitArtifacts(content)
    .map((segment) => {
      if (segment.kind === 'markdown') return segment.text;
      const a = segment.artifact;
      const lang = fenceLanguage(a);
      const body = a.content.replace(/\n+$/, '');
      const longest = Math.max(2, ...(body.match(/`+/g) ?? []).map((run) => run.length));
      const fence = '`'.repeat(longest + 1);
      const title = a.title ? `**${a.title}**\n\n` : '';
      return `\n${title}${fence}${lang}\n${body}\n${fence}\n`;
    })
    .join('');
}

function fenceLanguage(a: ParsedArtifact): string {
  switch (a.type) {
    case 'text/html':
      return 'html';
    case 'image/svg+xml':
      return 'svg';
    case 'text/mermaid':
      return 'mermaid';
    case 'text/markdown':
      return 'markdown';
    default:
      return a.language ?? '';
  }
}

/** Download file name for an artifact, e.g. `calculator.html`. */
export function artifactFileName(a: { title: string; identifier: string; type: ArtifactKind; language?: string }): string {
  const base =
    (a.title || a.identifier || 'artifact')
      .replace(/[\\/:*?"<>|]+/g, ' ')
      .replace(/\s+/g, ' ')
      .trim()
      .slice(0, 80) || 'artifact';
  const ext =
    a.type === 'text/html'
      ? 'html'
      : a.type === 'image/svg+xml'
        ? 'svg'
        : a.type === 'text/markdown'
          ? 'md'
          : a.type === 'text/mermaid'
            ? 'mmd'
            : extensionForLanguage(a.language);
  return ext === 'Dockerfile' ? 'Dockerfile' : `${base}.${ext}`;
}

/** MIME type used for the download blob. */
export function artifactMime(type: ArtifactKind): string {
  switch (type) {
    case 'text/html':
      return 'text/html;charset=utf-8';
    case 'image/svg+xml':
      return 'image/svg+xml;charset=utf-8';
    case 'text/markdown':
      return 'text/markdown;charset=utf-8';
    default:
      return 'text/plain;charset=utf-8';
  }
}

/** Preview type for a plain ```html / ```svg / ```xml(<svg) code block, or null. */
export function codeBlockPreviewType(language: string, code: string): ArtifactKind | null {
  const lang = language.trim().toLowerCase();
  if (lang === 'html' || lang === 'htm' || lang === 'xhtml') return 'text/html';
  if (lang === 'svg') return 'image/svg+xml';
  if (lang === 'xml' || lang === 'image/svg+xml') {
    const head = code.trimStart().slice(0, 300).toLowerCase();
    if (head.startsWith('<svg') || (head.startsWith('<?xml') && head.includes('<svg'))) return 'image/svg+xml';
  }
  return null;
}

/** Minimal HTML page around an SVG so it can be shown in the sandboxed preview frame. */
export function svgPreviewDocument(svg: string): string {
  return (
    '<!doctype html><html><head><meta charset="utf-8">' +
    '<meta name="viewport" content="width=device-width, initial-scale=1">' +
    '<style>html,body{margin:0;height:100%;background:#fff}' +
    'body{display:flex;align-items:center;justify-content:center;padding:16px;box-sizing:border-box}' +
    'svg{max-width:100%;max-height:100%;height:auto}</style></head><body>' +
    svg +
    '</body></html>'
  );
}
