import { useState, type ReactNode } from 'react';

/**
 * Renders a small, safe subset of Markdown used in model replies:
 * **bold**, `inline code`, *italic*, [links](url), ##/### headings, -/* and 1. lists,
 * GFM tables (| a | b | with a |---|---| separator row) and fenced code blocks (```lang ... ```).
 * Plain text is split into blocks; unclosed/unknown tokens are left as-is.
 */
/**
 * Short key terms ("«Agent»", "Flash") read as a pill badge; a longer emphasised phrase stays
 * plain bold so a whole sentence never turns into a badge. Mirrors the Claude inline-code look.
 */
function isTermBadge(text: string): boolean {
  const trimmed = text.trim();
  return trimmed.length > 0 && trimmed.length <= 32 && trimmed.split(/\s+/).length <= 4;
}

function renderInline(text: string): ReactNode[] {
  const out: ReactNode[] = [];
  // TABLES_GFM: the link pattern leads the alternation so `[text](url)` is not eaten by the
  // emphasis rules below. Parentheses are excluded from the URL on purpose: a `javascript:`
  // payload then fails to match at all and stays plain text, and a URL with brackets shows as-is
  // instead of being silently truncated mid-way.
  const regex = /(\[[^\]]*\]\([^()\s]*\)|\*\*[^*]+\*\*|`[^`]+`|\*[^*]+\*)/g;
  let last = 0;
  let m: RegExpExecArray | null;
  let i = 0;
  while ((m = regex.exec(text)) !== null) {
    if (m.index > last) out.push(text.slice(last, m.index));
    const token = m[0];
    if (token.startsWith('[')) {
      const split = token.indexOf('](');
      const label = token.slice(1, split);
      const url = token.slice(split + 2, -1);
      // Only real web links become anchors: a `javascript:` payload in model output stays text.
      out.push(
        /^(https?:\/\/|mailto:)/i.test(url) ? (
          <a key={i++} href={url} target="_blank" rel="noreferrer noopener">
            {label}
          </a>
        ) : (
          label
        ),
      );
    } else if (token.startsWith('**')) {
      const inner = token.slice(2, -2);
      out.push(
        <strong key={i++} className={isTermBadge(inner) ? 'font-semibold chat-term' : 'font-semibold'}>
          {inner}
        </strong>,
      );
    } else if (token.startsWith('`')) {
      out.push(
        <code key={i++} className="chat-code">
          {token.slice(1, -1)}
        </code>,
      );
    } else {
      out.push(<em key={i++}>{token.slice(1, -1)}</em>);
    }
    last = m.index + token.length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

type TableAlign = 'left' | 'center' | 'right';

interface ParsedTable {
  header: string[];
  aligns: TableAlign[];
  rows: string[][];
  endIndex: number;
}

/** Splits one GFM row on the pipe, dropping the optional leading/trailing pipes. */
function splitTableRow(line: string): string[] {
  let text = line.trim();
  if (text.startsWith('|')) text = text.slice(1);
  if (text.endsWith('|')) text = text.slice(0, -1);
  return text.split('|').map((cell) => cell.trim());
}

/** `:---` is left, `---:` is right, `:---:` is centred — same as GFM. */
function tableAlign(cell: string): TableAlign {
  const left = cell.startsWith(':');
  const right = cell.endsWith(':');
  if (left && right) return 'center';
  return right ? 'right' : 'left';
}

/**
 * TABLES_GFM: a table only starts when the line after the header is a delimiter row, which is what
 * keeps a stray `a | b` sentence from turning into a table.
 */
function isTableDelimiter(line: string | undefined): boolean {
  if (line === undefined) return false;
  const trimmed = line.trim();
  if (trimmed === '' || !trimmed.includes('|')) return false;
  const cells = splitTableRow(trimmed);
  return cells.length > 0 && cells.every((cell) => /^:?-+:?$/.test(cell));
}

function readTable(lines: string[], start: number): ParsedTable {
  const header = splitTableRow(lines[start]);
  const aligns = splitTableRow(lines[start + 1]).map(tableAlign);
  const rows: string[][] = [];
  let i = start + 2;
  while (i < lines.length && lines[i].trim() !== '' && lines[i].includes('|')) {
    rows.push(splitTableRow(lines[i]));
    i++;
  }
  return { header, aligns, rows, endIndex: i };
}

function TableBlock({ header, aligns, rows }: { header: string[]; aligns: TableAlign[]; rows: string[][] }) {
  return (
    // The wrapper is what scrolls horizontally: a wide table must never stretch the chat column.
    <div className="markdown__table">
      <table>
        <thead>
          <tr>
            {header.map((cell, idx) => (
              <th key={idx} style={{ textAlign: aligns[idx] ?? 'left' }}>
                {renderInline(cell)}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row, rowIdx) => (
            <tr key={rowIdx}>
              {header.map((_, colIdx) => (
                <td key={colIdx} style={{ textAlign: aligns[colIdx] ?? 'left' }}>
                  {/* A short row is padded rather than skipped, so columns stay aligned. */}
                  {renderInline(row[colIdx] ?? '')}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function CodeBlock({ language, code }: { language: string; code: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard may be unavailable (non-secure context); ignore.
    }
  }

  return (
    <div className="code-block">
      <div className="code-block__header">
        <span className="code-block__lang">{language || 'code'}</span>
        <button className="code-block__copy" onClick={() => void copy()} type="button">
          {copied ? 'Copied' : 'Copy'}
        </button>
      </div>
      <pre className="code-block__pre">
        <code>{code}</code>
      </pre>
    </div>
  );
}

export function Markdown({ text }: { text: string }) {
  const lines = text.split('\n');
  const blocks: ReactNode[] = [];

  let listItems: string[] = [];
  let listOrdered = false;
  let paragraph: string[] = [];
  let key = 0;

  const flushList = () => {
    if (listItems.length === 0) return;
    const items = listItems.map((li, idx) => <li key={idx}>{renderInline(li)}</li>);
    if (listOrdered) {
      blocks.push(
        <ol key={key++} className="list-decimal pl-5 space-y-1 my-1">
          {items}
        </ol>,
      );
    } else {
      blocks.push(
        <ul key={key++} className="list-disc pl-5 space-y-1 my-1">
          {items}
        </ul>,
      );
    }
    listItems = [];
  };

  const flushParagraph = () => {
    if (paragraph.length === 0) return;
    blocks.push(
      <p key={key++} className="my-1">
        {renderInline(paragraph.join(' '))}
      </p>,
    );
    paragraph = [];
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();

    const fence = trimmed.match(/^```(.*)$/);
    if (fence) {
      flushList();
      flushParagraph();
      const language = fence[1].trim();
      const codeLines: string[] = [];
      i++;
      while (i < lines.length && !lines[i].trim().startsWith('```')) {
        codeLines.push(lines[i]);
        i++;
      }
      blocks.push(<CodeBlock key={key++} language={language} code={codeLines.join('\n')} />);
      continue;
    }

    const heading = trimmed.match(/^(#{1,4})\s+(.*)$/);
    const ulItem = trimmed.match(/^[-*]\s+(.*)$/);
    const olItem = trimmed.match(/^\d+[.)]\s+(.*)$/);
    // TABLES_GFM: detected by look-ahead only — the delimiter row must follow the header directly.
    const table = trimmed.includes('|') && isTableDelimiter(lines[i + 1]) ? readTable(lines, i) : null;

    if (trimmed === '') {
      flushList();
      flushParagraph();
    } else if (heading) {
      flushList();
      flushParagraph();
      const level = heading[1].length;
      const Tag = level === 1 ? 'h2' : level === 2 ? 'h3' : 'h4';
      blocks.push(
        <Tag key={key++} className="font-semibold chat-text mt-3 mb-1 first:mt-0">
          {renderInline(heading[2])}
        </Tag>,
      );
    } else if (ulItem) {
      flushParagraph();
      listOrdered = false;
      listItems.push(ulItem[1]);
    } else if (olItem) {
      flushParagraph();
      listOrdered = true;
      listItems.push(olItem[1]);
    } else if (table) {
      flushList();
      flushParagraph();
      blocks.push(<TableBlock key={key++} header={table.header} aligns={table.aligns} rows={table.rows} />);
      // The body rows were consumed by the look-ahead; resume after the last of them.
      i = table.endIndex - 1;
    } else {
      flushList();
      paragraph.push(trimmed);
    }
  }
  flushList();
  flushParagraph();

  return <div className="markdown">{blocks}</div>;
}
