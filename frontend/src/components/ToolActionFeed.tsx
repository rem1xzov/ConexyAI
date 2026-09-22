import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { CommandDecisionHandler, ToolActionEvent } from '../types/signalr';
import { CommandConfirmCard } from './CommandConfirmCard';

const MAX_VISIBLE = 20;

// TOOL_PILLS: добавлено 2026-09-20
// The backend emits a `started` event and then a terminal one (completed/failed) per tool
// invocation. The agent runs tool calls strictly one at a time, so pairing them in order is
// enough to turn two transport events into one user-facing pill.
type PillStatus = 'running' | 'completed' | 'failed' | 'rejected';

interface ToolPill {
  key: string;
  toolName: string;
  icon: string;
  label: string;
  status: PillStatus;
  /** Result snippet (found documents, console output) — opened from the pill. */
  detail?: string;
  /** Extra line for a failed pill. */
  errorLine?: string;
}

function pillKey(event: ToolActionEvent): string {
  return `${event.toolName}|${event.path}|${event.command}`;
}

function toolIcon(toolName: string, command: string): string {
  if (toolName === 'bash') return '⚡';
  if (toolName === 'search_documents') return '🕒';
  if (toolName === 'web_search') return '🌐';
  if (toolName === 'terminal_exec') return '⚡';
  if (toolName === 'file_write' || toolName === 'file_patch') return '📄';
  if (toolName === 'str_replace_editor') {
    switch (command) {
      case 'view':
        return '👁';
      case 'create':
        return '📄';
      case 'str_replace':
      case 'insert':
        return '🔧';
      case 'undo':
        return '↩';
      default:
        return '🔧';
    }
  }
  return '🔧';
}

function statusFrom(event: ToolActionEvent): PillStatus {
  if (event.status === 'completed') return 'completed';
  if (event.status === 'failed') return 'failed';
  if (event.status === 'rejected') return 'rejected';
  return 'running';
}

/** COMMAND_CONFIRM: bash commands needing a decision are rendered as their own card. */
function isConfirmableCommand(event: ToolActionEvent): boolean {
  return event.toolName === 'bash' && event.pendingActionId != null;
}

/** Synthesised pill for agent stages that have no ToolAction row of their own. */
export interface ToolStatusPill {
  label: string;
}

interface ToolActionFeedProps {
  actions: ToolActionEvent[];
  /** Inline command confirmation handler; omit to render the feed read-only. */
  onCommandDecision?: CommandDecisionHandler;
  /** Live agent status, shown as a running pill when no tool event covers it. */
  statusPill?: ToolStatusPill | null;
  // PANEL_NO_CARDS: добавлено 2026-09-22
  /**
   * When false, commands awaiting confirmation are rendered as plain status pills instead of
   * decision cards. The agent workspace panel uses this so the cards (and their Allow/Deny
   * buttons) exist in exactly one place — the chat feed.
   */
  showConfirmCards?: boolean;
}

export function ToolActionFeed({
  actions,
  onCommandDecision,
  statusPill,
  showConfirmCards = true,
}: ToolActionFeedProps) {
  const { t } = useTranslation();
  const [openPills, setOpenPills] = useState<Record<string, boolean>>({});

  const tail = actions.slice(-MAX_VISIBLE);

  const confirmCards = new Map<string, ToolActionEvent[]>();
  const pills: ToolPill[] = [];
  const openByKey = new Map<string, ToolPill>();

  for (const a of tail) {
    if (showConfirmCards && isConfirmableCommand(a)) {
      const key = a.pendingActionId!;
      const list = confirmCards.get(key) ?? [];
      list.push(a);
      confirmCards.set(key, list);
      continue;
    }

    // PANEL_NO_CARDS: without cards, a pending_confirmation event has no "started" twin to pair
    // with, so it starts its own pill and later completion/rejection events settle it.
    const key = pillKey(a);
    if (a.status === 'started' || (!showConfirmCards && a.status === 'pending_confirmation')) {
      const pill: ToolPill = {
        key: `${key}#${pills.length}`,
        toolName: a.toolName,
        icon: toolIcon(a.toolName, a.command),
        label: pillLabel(a, t),
        status: 'running',
      };
      pills.push(pill);
      openByKey.set(key, pill);
      continue;
    }

    const open = openByKey.get(key);
    if (open) {
      open.status = statusFrom(a);
      open.detail = a.output ?? open.detail;
      open.errorLine = open.status === 'failed' ? a.summary : undefined;
      openByKey.delete(key);
      continue;
    }

    // A completion whose `started` event fell outside the visible tail (or was never sent).
    pills.push({
      key: `${key}#${pills.length}`,
      toolName: a.toolName,
      icon: toolIcon(a.toolName, a.command),
      label: pillLabel(a, t),
      status: statusFrom(a),
      detail: a.output,
      errorLine: a.status === 'failed' ? a.summary : undefined,
    });
  }

  const hasRunningPill = pills.some((p) => p.status === 'running') || confirmCards.size > 0;
  const showStatusPill = Boolean(statusPill) && !hasRunningPill;

  if (pills.length === 0 && confirmCards.size === 0 && !showStatusPill) return null;

  return (
    <div className="tool-feed">
      {pills.map((pill) => {
        const open = Boolean(openPills[pill.key]);
        const expandable = Boolean(pill.detail);
        return (
          <div key={pill.key} className={`tool-pill tool-pill--${pill.status}`}>
            <button
              type="button"
              className={`tool-pill__head ${expandable ? 'tool-pill__head--actionable' : ''}`}
              onClick={() => expandable && setOpenPills((prev) => ({ ...prev, [pill.key]: !prev[pill.key] }))}
              aria-expanded={expandable ? open : undefined}
            >
              <span className="tool-pill__icon">{pill.icon}</span>
              <span className="tool-pill__label" title={pill.label}>
                {pill.label}
              </span>
              {pill.errorLine && <span className="tool-pill__error">{pill.errorLine}</span>}
              {pill.status === 'running' ? (
                <span className="tool-pill__spinner" aria-hidden="true" />
              ) : (
                <span className="tool-pill__mark">{pillMark(pill.status)}</span>
              )}
              {expandable && <span className={`tool-pill__chevron ${open ? 'tool-pill__chevron--open' : ''}`}>▾</span>}
            </button>

            {expandable && open && <pre className="tool-pill__body">{pill.detail}</pre>}
          </div>
        );
      })}

      {/* Rendered after the pills so the running light in the message flow sits under the
          last one. */}
      {showStatusPill && statusPill && (
        <div className="tool-pill tool-pill--running" role="status">
          <div className="tool-pill__head">
            <span className="tool-pill__icon">🕒</span>
            <span className="tool-pill__label">{statusPill.label}</span>
            <span className="tool-pill__spinner" aria-hidden="true" />
          </div>
        </div>
      )}

      {Array.from(confirmCards.entries()).map(([key, events]) => (
        <CommandConfirmCard key={`confirm-${key}`} events={events} onDecision={onCommandDecision} />
      ))}
    </div>
  );
}

function pillMark(status: PillStatus): JSX.Element {
  if (status === 'completed') return <span className="chat-ok">✓</span>;
  if (status === 'rejected') return <span className="chat-danger">⊘</span>;
  return <span className="chat-danger">✕</span>;
}

function pillLabel(event: ToolActionEvent, t: (key: string) => string): string {
  switch (event.toolName) {
    case 'bash':
      return event.command;
    case 'terminal_exec':
      return event.command;
    case 'search_documents':
      return `${t('toolPill.searchDocs')}: ${event.command}`;
    case 'web_search':
      return `${t('toolPill.searchWeb')}: ${event.command}`;
    case 'str_replace_editor':
      return `${event.command} ${event.path}`;
    default:
      return event.summary || `${event.toolName} ${event.command}`;
  }
}
