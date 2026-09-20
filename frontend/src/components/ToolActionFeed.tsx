import { useTranslation } from 'react-i18next';
import type { CommandApproval, ToolActionEvent } from '../types/signalr';
import { CommandConfirmCard } from './CommandConfirmCard';

const MAX_VISIBLE = 20;

function toolIcon(event: ToolActionEvent): string {
  if (event.toolName === 'bash') return '▶';
  switch (event.command) {
    case 'view':
      return '👁';
    case 'create':
      return '📄';
    case 'str_replace':
      return '🔧';
    case 'insert':
      return '➕';
    case 'undo':
      return '↩';
    default:
      return '🔧';
  }
}

function statusMark(status: ToolActionEvent['status']): JSX.Element {
  if (status === 'completed') return <span className="chat-ok">✓</span>;
  if (status === 'failed') return <span className="chat-danger">✕</span>;
  if (status === 'rejected') return <span className="chat-danger">⊘</span>;
  if (status === 'pending_confirmation') return <span className="chat-warn animate-pulse">⚠</span>;
  return <span className="chat-warn animate-pulse">●</span>;
}

// COMMAND_CONFIRM: добавлено 2026-09-20
// Every agent bash command is confirmed now, so the card is keyed on the pending-action id
// rather than on "is this command dangerous". Dangerous commands only get the accent.
function isConfirmableCommand(event: ToolActionEvent): boolean {
  return event.toolName === 'bash' && event.pendingActionId != null;
}

interface ToolActionFeedProps {
  actions: ToolActionEvent[];
  /** Inline command confirmation handlers; omit to render the feed read-only. */
  approval?: CommandApproval;
}

export function ToolActionFeed({ actions, approval }: ToolActionFeedProps) {
  const { t } = useTranslation();
  const tail = actions.slice(-MAX_VISIBLE);
  if (tail.length === 0 && !approval?.allowAllEnabled) return null;

  // Confirmation cards are grouped by pending-action id. Grouping over the visible tail is
  // enough: the agent is blocked while a command awaits a decision, so the pending event is
  // always the most recent one. Older commands fall back to plain rows, which keeps a long
  // autonomous run readable.
  const confirmCards = new Map<string, ToolActionEvent[]>();
  for (const a of tail) {
    if (!isConfirmableCommand(a)) continue;
    const key = a.pendingActionId!;
    const list = confirmCards.get(key) ?? [];
    list.push(a);
    confirmCards.set(key, list);
  }

  const ordinary = tail.filter((a) => !isConfirmableCommand(a));

  return (
    <div className="my-3 space-y-3">
      {/* COMMAND_CONFIRM: the user chose to skip further prompts for this task. */}
      {approval?.allowAllEnabled && (
        <div className="cmd-confirm cmd-confirm--auto">
          <span className="cmd-confirm__auto-text">{t('cmdConfirm.allowAllActive')}</span>
          <button className="cmd-confirm__auto-off" onClick={approval.onDisableAllowAll} type="button">
            {t('cmdConfirm.disableAllowAll')}
          </button>
        </div>
      )}

      {ordinary.length > 0 && (
        <div className="rounded-lg chat-surface-soft p-3 text-xs">
          <div className="chat-muted font-medium mb-2">{t('toolAction.liveStatus')}</div>
          <ul className="space-y-1.5">
            {ordinary.map((a, i) => (
              <li key={`${a.toolName}-${a.command}-${i}`} className="tool-action flex items-center gap-2 chat-text">
                <span className="w-4 text-center shrink-0">{toolIcon(a)}</span>
                <span className="flex-1 min-w-0 truncate font-mono" title={a.summary}>
                  {a.summary || `${a.toolName} ${a.command}`}
                </span>
                {a.isDangerous && (
                  <span className="cmd-confirm__row-badge" title={t('cmdConfirm.dangerous')}>
                    ⚠
                  </span>
                )}
                <span className="shrink-0 w-4 text-center">{statusMark(a.status)}</span>
              </li>
            ))}
          </ul>
        </div>
      )}

      {Array.from(confirmCards.entries()).map(([key, events]) => (
        <CommandConfirmCard key={`confirm-${key}`} events={events} approval={approval} />
      ))}
    </div>
  );
}
