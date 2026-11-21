import { Fragment, memo, useMemo, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { inlineText, parseInline, parseMarkdown, type Block, type Inline, type TableAlign } from './markdown/parse';
import { CodeBlock } from './markdown/CodeBlock';
import { MathTex } from './markdown/Math';
import { MermaidDiagram } from './markdown/MermaidDiagram';

/**
 * Renders the Markdown of model replies (see `markdown/parse.ts` for the supported subset).
 *
 * MARKDOWN_M23: переписано 2026-09-24 — разбор вынесен в отдельный парсер (CommonMark-правила для
 * огороженного кода, вложенных списков, `start` у нумерованных и flanking-разделителей), здесь
 * остался только вывод. Весь текст модели попадает в DOM как текстовые узлы React; единственные
 * исключения — HTML от KaTeX (trust:false) и схема mermaid, показанная через <img>.
 */

/**
 * Short key terms ("«Agent»", "Flash") read as a pill badge; a longer emphasised phrase stays
 * plain bold so a whole sentence never turns into a badge. Mirrors the Claude inline-code look.
 */
function isTermBadge(text: string): boolean {
  const trimmed = text.trim();
  return trimmed.length > 0 && trimmed.length <= 32 && trimmed.split(/\s+/).length <= 4;
}

/**
 * LINKIFY: голые URL в ответах модели (Smart Search, ссылки на источники) раньше были обычным
 * текстом — теперь это кликабельные ссылки. Знаки препинания в конце URL не входят в href.
 */
const BARE_URL = /(https?:\/\/[^\s<>"']+)/g;

function renderTextWithLinks(value: string, streaming = false): ReactNode {
  // TOKEN_FADE: только во время активного стрима разбиваем текст на слова.
  // display:inline (не inline-block) — иначе слова рвутся на переносе («Ap / ple»).
  if (streaming) {
    return value.split(/(\s+)/).map((part, index) => {
      if (!part) return null;
      if (/^\s+$/.test(part)) return part;
      return (
        <span key={index} className="stream-word">
          {part}
        </span>
      );
    });
  }
  if (!value.includes('http')) return value;
  const parts = value.split(BARE_URL);
  return parts.map((part, index) => {
    if (index % 2 === 0) return part;
    const trailing = part.match(/[.,;:!?)\]»]+$/)?.[0] ?? '';
    const href = trailing ? part.slice(0, -trailing.length) : part;
    return (
      <span key={`url-${index}`}>
        <a className="chat-link" href={href} target="_blank" rel="noreferrer noopener">
          {href}
        </a>
        {trailing}
      </span>
    );
  });
}

function renderInlineNodes(nodes: Inline[], streaming = false): ReactNode[] {
  return nodes.map((node, i) => {
    switch (node.type) {
      case 'text':
        return <Fragment key={i}>{renderTextWithLinks(node.value, streaming)}</Fragment>;
      case 'code':
        return (
          <code key={i} className="chat-code">
            {node.value}
          </code>
        );
      case 'math':
        return <MathTex key={i} tex={node.value} display={node.display} />;
      case 'br':
        return <br key={i} />;
      case 'link':
        return (
          <a key={i} href={node.href} target="_blank" rel="noreferrer noopener">
            {renderInlineNodes(node.children, streaming)}
          </a>
        );
      case 'strong':
        return (
          <strong key={i} className={isTermBadge(inlineText(node.children)) ? 'font-semibold chat-term' : 'font-semibold'}>
            {renderInlineNodes(node.children, streaming)}
          </strong>
        );
      case 'em':
        return <em key={i}>{renderInlineNodes(node.children, streaming)}</em>;
      case 'del':
        return <del key={i}>{renderInlineNodes(node.children, streaming)}</del>;
      default:
        return null;
    }
  });
}

function InlineText({ text, streaming = false }: { text: string; streaming?: boolean }) {
  const nodes = useMemo(() => parseInline(text), [text]);
  return <>{renderInlineNodes(nodes, streaming)}</>;
}

function TableBlock({ header, aligns, rows, streaming = false }: { header: string[]; aligns: TableAlign[]; rows: string[][]; streaming?: boolean }) {
  return (
    // The wrapper is what scrolls horizontally: a wide table must never stretch the chat column.
    // DRAWER_TABLE: data-no-drawer-swipe — свайп по таблице никогда не открывает боковое меню.
    <div className="markdown__table" data-no-drawer-swipe="true">
      <table>
        <thead>
          <tr>
            {header.map((cell, idx) => (
              <th key={idx} style={{ textAlign: aligns[idx] ?? 'left' }}>
                <InlineText text={cell} streaming={streaming} />
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
                  <InlineText text={row[colIdx] ?? ''} streaming={streaming} />
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

interface RenderContext {
  /** Tight list items render their paragraphs inline (no paragraph margins). */
  tight: boolean;
  /** STREAMING_WORDS: включать плавное появление слов по границам. */
  streaming: boolean;
  taskLabels: { done: string; todo: string };
}

function renderBlocks(blocks: Block[], ctx: RenderContext): ReactNode[] {
  return blocks.map((block, key) => {
    switch (block.type) {
      case 'paragraph':
        return ctx.tight ? (
          <div key={key} className="markdown__tight">
            <InlineText text={block.text} streaming={ctx.streaming} />
          </div>
        ) : (
          <p key={key} className="my-1">
            <InlineText text={block.text} streaming={ctx.streaming} />
          </p>
        );
      case 'heading': {
        const Tag = block.level === 1 ? 'h2' : block.level === 2 ? 'h3' : 'h4';
        return (
          <Tag key={key} className="font-semibold chat-text mt-3 mb-1 first:mt-0">
            <InlineText text={block.text} streaming={ctx.streaming} />
          </Tag>
        );
      }
      case 'code':
        if (block.lang.toLowerCase() === 'mermaid') {
          return <MermaidDiagram key={key} code={block.code} closed={block.closed} />;
        }
        return <CodeBlock key={key} language={block.lang} code={block.code} closed={block.closed} />;
      case 'math':
        return <MathTex key={key} tex={block.tex} display block />;
      case 'hr':
        return <hr key={key} className="markdown__hr" />;
      case 'blockquote':
        return (
          <blockquote key={key} className="markdown__quote">
            {renderBlocks(block.children, { ...ctx, tight: false })}
          </blockquote>
        );
      case 'table':
        return <TableBlock key={key} header={block.header} aligns={block.aligns} rows={block.rows} streaming={ctx.streaming} />;
      case 'list': {
        const items = block.items.map((item, idx) => (
          <li key={idx} className={item.checked !== undefined ? 'markdown__task' : undefined}>
            {item.checked !== undefined && (
              <input
                type="checkbox"
                className="markdown__checkbox"
                checked={item.checked}
                readOnly
                disabled
                aria-label={item.checked ? ctx.taskLabels.done : ctx.taskLabels.todo}
              />
            )}
            {renderBlocks(item.blocks, { ...ctx, tight: block.tight })}
          </li>
        ));
        const isTaskList = block.items.every((item) => item.checked !== undefined);
        if (block.ordered) {
          return (
            <ol
              key={key}
              className="list-decimal pl-5 space-y-1 my-1 markdown__list"
              start={block.start !== 1 ? block.start : undefined}
            >
              {items}
            </ol>
          );
        }
        return (
          <ul key={key} className={`${isTaskList ? 'markdown__tasks' : 'list-disc'} pl-5 space-y-1 my-1 markdown__list`}>
            {items}
          </ul>
        );
      }
      default:
        return null;
    }
  });
}

interface MarkdownProps {
  text: string;
  /** Активный стрим: слова появляются через tokenFadeIn; история — без анимации. */
  streaming?: boolean;
}

export const Markdown = memo(function Markdown({ text, streaming = false }: MarkdownProps) {
  const { t } = useTranslation();
  const blocks = useMemo(() => parseMarkdown(text), [text]);
  const ctx: RenderContext = {
    tight: false,
    streaming,
    taskLabels: { done: t('render.taskDone'), todo: t('render.taskTodo') },
  };
  return <div className="markdown">{renderBlocks(blocks, ctx)}</div>;
});
