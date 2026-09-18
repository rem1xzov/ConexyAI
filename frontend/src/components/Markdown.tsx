import { useState, type ReactNode } from 'react';

/**
 * Renders a small, safe subset of Markdown used in model replies:
 * **bold**, `inline code`, *italic*, ##/### headings, -/* and 1. lists,
 * and fenced code blocks (```lang ... ```).
 * Plain text is split into blocks; unclosed/unknown tokens are left as-is.
 */
function renderInline(text: string): ReactNode[] {
  const out: ReactNode[] = [];
  const regex = /(\*\*[^*]+\*\*|`[^`]+`|\*[^*]+\*)/g;
  let last = 0;
  let m: RegExpExecArray | null;
  let i = 0;
  while ((m = regex.exec(text)) !== null) {
    if (m.index > last) out.push(text.slice(last, m.index));
    const token = m[0];
    if (token.startsWith('**')) {
      out.push(
        <strong key={i++} className="font-semibold">
          {token.slice(2, -2)}
        </strong>,
      );
    } else if (token.startsWith('`')) {
      out.push(
        <code key={i++} className="font-mono text-[0.9em] bg-zinc-800/80 px-1 py-0.5 rounded text-zinc-200">
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

    if (trimmed === '') {
      flushList();
      flushParagraph();
    } else if (heading) {
      flushList();
      flushParagraph();
      const level = heading[1].length;
      const Tag = level === 1 ? 'h2' : level === 2 ? 'h3' : 'h4';
      blocks.push(
        <Tag key={key++} className="font-semibold text-zinc-100 mt-3 mb-1 first:mt-0">
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
    } else {
      flushList();
      paragraph.push(trimmed);
    }
  }
  flushList();
  flushParagraph();

  return <div className="markdown">{blocks}</div>;
}
