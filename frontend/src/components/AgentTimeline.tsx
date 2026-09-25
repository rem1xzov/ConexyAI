import { useTranslation } from 'react-i18next';
import type { TurnBlock } from '../types/chat';
import type { CommandDecisionHandler, ToolActionEvent } from '../types/signalr';
import { ActionStepLine, type ActionKind, type ActionState } from './ActionStepLine';
import { CommandConfirmCard } from './CommandConfirmCard';
import { ThinkingStep } from './ThinkingStep';
import { TodoPanel } from './TodoPanel';
import { AssistantContent } from './artifacts/AssistantContent';
import { isTerminalToolStatus, toolEventFromBlock } from '../utils/turnBlocks';

// INTERLEAVED_STREAM: переписано 2026-09-24
//
// Раньше лента собиралась из двух независимых наборов — размышления (`steps`) и действия
// (`toolActions`) — и склеивала их по счётчику вызовов, а текст ответа рендерился отдельно, одним
// куском внизу сообщения. Из-за этого короткие реплики модели между инструментами оказывались
// приклеены к финальному ответу, а инструменты висели над текстом вне контекста.
//
// Теперь источник правды — хронологический массив `message.blocks` (см. `utils/turnBlocks`), и
// лента просто рендерит его по порядку: размышление → текст → инструмент → текст → команда → ответ.
// Плоские поля (`steps`, `toolActions`, `content`) продолжают заполняться для остальных
// потребителей — выгрузки, карточек файлов, плана в IDE, — но лента на них больше не опирается.

type Translate = (key: string, options?: Record<string, unknown>) => string;

interface AgentTimelineProps {
  /** Chronological feed of the turn (see `utils/turnBlocks`; `MessageBubble` builds it). */
  blocks: TurnBlock[];
  /** Owns this feed — text blocks go through the artifact-aware renderer under this id. */
  messageId: string;
  createdAt: number;
  /** True while the turn is still running — the last thought block shows a live timer. */
  streaming?: boolean;
  /** Inline decision handler for commands that require approval; omit for a read-only view. */
  onCommandDecision?: CommandDecisionHandler;
  /** Live status shown when no block is currently running. */
  statusPill?: { label: string } | null;
  /** Opens a path from an action row in the workspace editor. */
  onOpenPath?: (path: string) => void;
}

function stateFrom(event: ToolActionEvent): ActionState {
  if (event.status === 'completed') return 'completed';
  if (event.status === 'failed') return 'failed';
  if (event.status === 'rejected') return 'rejected';
  return 'running';
}

/** Host of a URL ("https://www.example.com/a" -> "example.com"); the raw text when it is not a URL. */
function urlHost(value: string): string {
  try {
    const host = new URL(value).host;
    return host.replace(/^www\./, '') || value;
  } catch {
    return value;
  }
}

interface ActionDescriptor {
  action: ActionKind;
  /** The short line itself, e.g. "Читаю" or "Поиск: StudentsMode". */
  label: string;
  /** File the action touched, when there is one worth linking. */
  path?: string;
}

/**
 * Turns a raw tool invocation into the short description the reference feed uses
 * ("Read ConexyAgentRunner.cs" instead of the whole shell line where that is obvious).
 */
function describeAction(event: ToolActionEvent, t: Translate): ActionDescriptor {
  const command = (event.command ?? '').trim();

  if (event.toolName === 'str_replace_editor') {
    const read = command === 'view';
    return {
      action: read ? 'read_file' : 'edit_file',
      label: read ? t('toolPill.readFile') : t('toolPill.editFile'),
      path: event.path,
    };
  }

  if (event.toolName === 'search_documents') {
    return { action: 'search', label: `${t('toolPill.searchDocs')}: ${command}` };
  }

  if (event.toolName === 'web_search') {
    return { action: 'web', label: `${t('toolPill.searchWeb')}: ${command}` };
  }

  // DEEP_RESEARCH: добавлено 2026-09-24 — агент читает страницу из выдачи (command = URL);
  // в строке показываем только хост, полный адрес длинный и ничего не добавляет.
  if (event.toolName === 'fetch_web_page') {
    return { action: 'web', label: t('render.toolFetchPage', { host: urlHost(command) }) };
  }

  // CHAT_SEARCH: добавлено 2026-09-24 — поиск по прошлым диалогам пользователя (command = запрос).
  if (event.toolName === 'search_user_chats') {
    return { action: 'search', label: t('render.toolSearchChats', { query: command }) };
  }

  if (event.toolName === 'bash' || event.toolName === 'terminal_exec') {
    const read = /^(?:cat|head|tail|bat)\s+(?:-[^\s]+\s+)*(\S+)\s*$/.exec(command);
    if (read) return { action: 'read_file', label: t('toolPill.readFile'), path: read[1] };
    if (/^(?:ls|dir|tree)\b/.test(command))
      return { action: 'list', label: `${t('toolPill.listFiles')}: ${command}` };
    if (/^(?:grep|rg|ag|find|fd)\b/.test(command))
      return { action: 'search', label: `${t('toolPill.search')}: ${command}` };
    return { action: 'command', label: command };
  }

  return { action: 'command', label: event.summary || `${event.toolName} ${command}`.trim() };
}

export function AgentTimeline({
  blocks,
  messageId,
  createdAt,
  streaming = false,
  onCommandDecision,
  statusPill,
  onOpenPath,
}: AgentTimelineProps) {
  const { t } = useTranslation();

  const feed = blocks;
  if (feed.length === 0 && !statusPill) return null;

  const lastIndex = feed.length - 1;
  const lastBlock = feed[lastIndex];
  // One "working" indicator at a time: the live status row waits until nothing else is running.
  const somethingRunning = feed.some((block, index) => {
    if (block.type === 'tool') return !isTerminalToolStatus(block.status);
    // A thought counts as running only while it is the live one; a block left open by a finished
    // turn must not keep the status row hidden forever.
    if (block.type === 'thought') return streaming && index === lastIndex && block.durationMs === undefined;
    return false;
  });
  const showStatusRow = Boolean(statusPill) && !somethingRunning;

  return (
    <div className="agent-timeline">
      {feed.map((block, index) => {
        switch (block.type) {
          case 'thought':
            return (
              <ThinkingStep
                key={block.id}
                text={block.content}
                // Only the very last block of a running turn is still being written in.
                running={index === lastIndex && streaming && block.durationMs === undefined}
              />
            );

          case 'text':
            return (
              // ARTIFACTS: text goes through the artifact-aware renderer, so a `<conexy_artifact>` tag
              // in an agent's own line becomes a card instead of leaking into the feed as raw markup.
              // The block id scopes this block's artifacts, so two text blocks of one message do not
              // overwrite each other in the panel.
              <div key={block.id} className="agent-feed__text chat-text">
                <AssistantContent
                  messageId={`${messageId}#${block.id}`}
                  createdAt={createdAt}
                  content={block.content}
                  // Only the newest text block of a running turn is still being written into.
                  streaming={index === lastIndex && streaming}
                />
              </div>
            );

          case 'plan':
            return <TodoPanel key={block.id} todos={block.items} />;

          case 'tool': {
            // A command that went through approval keeps the full terminal card for its whole life —
            // request, decision and output all belong together, and switching it to a compact row the
            // moment it finishes would both lose the card's output panel and jump the feed.
            if (block.actionId) {
              return (
                <CommandConfirmCard
                  key={block.id}
                  events={[toolEventFromBlock(block)]}
                  onDecision={onCommandDecision}
                />
              );
            }

            const event = toolEventFromBlock(block);
            const { action, label, path } = describeAction(event, t);
            const state = stateFrom(event);
            return (
              <ActionStepLine
                key={block.id}
                kind={action}
                label={label}
                path={path}
                onOpenPath={onOpenPath}
                state={state}
                meta={state === 'completed' ? block.summary : undefined}
                errorLine={state === 'failed' ? block.summary : undefined}
                detail={block.output}
              />
            );
          }

          default:
            return null;
        }
      })}

      {showStatusRow && statusPill && (
        <div className="tl-step tl-step--running" role="status">
          <span className="tl-step__pulse" aria-hidden="true" />
          <span className="tl-step__label">{statusPill.label}</span>
        </div>
      )}
    </div>
  );
}

/**
 * What the feed already renders, so `MessageBubble` does not repeat it: the reasoning accordion, the
 * top-of-message plan and the standalone answer bubble all step aside once their block is present.
 */
export function feedSummary(blocks: TurnBlock[] | undefined): {
  thoughts: boolean;
  plan: boolean;
  text: boolean;
} {
  const list = blocks ?? [];
  return {
    thoughts: list.some((b) => b.type === 'thought'),
    plan: list.some((b) => b.type === 'plan'),
    text: list.some((b) => b.type === 'text'),
  };
}
