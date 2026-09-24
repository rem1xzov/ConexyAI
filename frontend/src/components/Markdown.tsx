import { memo, useMemo, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { inlineText, parseInline, parseMarkdown, type Block, type Inline, type TableAlign } from './markdown/parse';
import { CodeBlock } from './markdown/CodeBlock';

/**
 * Renders the Markdown of model replies (see `markdown/parse.ts` for the supported subset).
 *
 * MARKDOWN_M23: переписано 2026-09-24 — разбор вынесен в отдельный парсер (CommonMark-правила для
 * огороженного кода, вложенных списков, `start` у нумерованных и flanking-разделителей), здесь
 * остался только вывод. Весь текст модели попадает в DOM как текстовые узлы React.
 */

/**
 * Short key terms ("«Agent»", "Flash") read as a pill badge; a longer emphasised phrase stays
 * plain bold so a whole sentence never turns into a badge. Mirrors the Claude inline-code look.
 */
function isTermBadge(text: string): boolean {
  const trimmed = text.trim();
  return trimmed.length > 0 && trimmed.length <= 32 && trimmed.split(/\s+/).length <= 4;
}

function renderInlineNodes(nodes: Inline[]): ReactNode[] {
  return nodes.map((node, i) => {
    switch (node.type) {
      case 'text':
        return node.value;
      case 'code':
        return (
          <code key={i} className="chat-code">
            {node.value}
          </code>
        );
      case 'math':
        return node.display ? `$$${node.value}$$` : `$${node.value}$`;
      case 'br':
        return <br key={i} />;
      case 'link':
        return (
          <a key={i} href={node.href} target="_blank" rel="noreferrer noopener">
            {renderInlineNodes(node.children)}
          </a>
        );
      case 'strong':
        return (
          <strong key={i} className={isTermBadge(inlineText(node.children)) ? 'font-semibold chat-term' : 'font-semibold'}>
            {renderInlineNodes(node.children)}
          </strong>
        );
      case 'em':
        return <em key={i}>{renderInlineNodes(node.children)}</em>;
      case 'del':
        return <del key={i}>{renderInlineNodes(node.children)}</del>;
      default:
        return null;
    }
  });
}

function InlineText({ text }: { text: string }) {
  const nodes = useMemo(() => parseInline(text), [text]);
  return <>{renderInlineNodes(nodes)}</>;
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
                <InlineText text={cell} />
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
                  <InlineText text={row[colIdx] ?? ''} />
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
  taskLabels: { done: string; todo: string };
}

function renderBlocks(blocks: Block[], ctx: RenderContext): ReactNode[] {
  return blocks.map((block, key) => {
    switch (block.type) {
      case 'paragraph':
        return ctx.tight ? (
          <div key={key} className="markdown__tight">
            <InlineText text={block.text} />
          </div>
        ) : (
          <p key={key} className="my-1">
            <InlineText text={block.text} />
          </p>
        );
      case 'heading': {
        const Tag = block.level === 1 ? 'h2' : block.level === 2 ? 'h3' : 'h4';
        return (
          <Tag key={key} className="font-semibold chat-text mt-3 mb-1 first:mt-0">
            <InlineText text={block.text} />
          </Tag>
        );
      }
      case 'code':
        return <CodeBlock key={key} language={block.lang} code={block.code} closed={block.closed} />;
      case 'math':
        return (
          <p key={key} className="my-1">
            {`$$${block.tex}$$`}
          </p>
        );
      case 'hr':
        return <hr key={key} className="markdown__hr" />;
      case 'blockquote':
        return (
          <blockquote key={key} className="markdown__quote">
            {renderBlocks(block.children, { ...ctx, tight: false })}
          </blockquote>
        );
      case 'table':
        return <TableBlock key={key} header={block.header} aligns={block.aligns} rows={block.rows} />;
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
}

export const Markdown = memo(function Markdown({ text }: MarkdownProps) {
  const { t } = useTranslation();
  const blocks = useMemo(() => parseMarkdown(text), [text]);
  const ctx: RenderContext = {
    tight: false,
    taskLabels: { done: t('render.taskDone'), todo: t('render.taskTodo') },
  };
  return <div className="markdown">{renderBlocks(blocks, ctx)}</div>;
});
