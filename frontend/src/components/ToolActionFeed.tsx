import { useState } from 'react';
import type { ToolActionEvent } from '../types/signalr';

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
  if (status === 'completed') return <span className="text-emerald-400">✓</span>;
  if (status === 'failed') return <span className="text-red-400">✕</span>;
  if (status === 'rejected') return <span className="text-red-400">⊘</span>;
  if (status === 'pending_confirmation') return <span className="text-amber-400 animate-pulse">⚠</span>;
  return <span className="text-amber-400 animate-pulse">●</span>;
}

// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
function isDangerous(event: ToolActionEvent): boolean {
  return event.toolName === 'bash' && (event.pendingActionId != null || event.status === 'rejected');
}

interface DangerousCommandCardProps {
  events: ToolActionEvent[];
}

function DangerousCommandCard({ events }: DangerousCommandCardProps) {
  const [open, setOpen] = useState(false);

  const first = events[0];
  const completed = events.find((e) => e.status === 'completed' || e.status === 'failed');
  const rejected = events.find((e) => e.status === 'rejected');

  const status: ToolActionEvent['status'] = rejected
    ? 'rejected'
    : completed
      ? completed.status
      : 'pending_confirmation';

  const label =
    status === 'completed'
      ? 'Выполнено'
      : status === 'failed'
        ? 'Ошибка'
        : status === 'rejected'
          ? 'Отклонено пользователем'
          : 'Ожидает подтверждения';

  const output = completed?.output ?? '';

  return (
    <div className={`danger-cmd-feed-card danger-cmd-feed-card--${status}`}>
      <div className="danger-cmd-feed-row">
        <span className="danger-cmd-feed-status">{statusMark(status)}</span>
        <code className="danger-cmd-feed-command">{first.command}</code>
        <span className="danger-cmd-feed-label">{label}</span>
      </div>

      {first.workingDirectory && (
        <div className="danger-cmd-feed-dir" title={first.workingDirectory}>
          {first.workingDirectory}
        </div>
      )}

      {output && (
        <div className="danger-cmd-feed-output">
          <button
            className="danger-cmd-feed-toggle"
            onClick={() => setOpen((o) => !o)}
            type="button"
          >
            {open ? '▾ Скрыть вывод' : '▸ Показать вывод'}
          </button>
          {open && <pre className="danger-cmd-feed-pre">{output}</pre>}
        </div>
      )}
    </div>
  );
}

interface ToolActionFeedProps {
  actions: ToolActionEvent[];
}

export function ToolActionFeed({ actions }: ToolActionFeedProps) {
  const tail = actions.slice(-MAX_VISIBLE);
  if (tail.length === 0) return null;

  // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
  const dangerous = new Map<string, ToolActionEvent[]>();
  const ordinary: ToolActionEvent[] = [];

  for (const a of tail) {
    if (isDangerous(a)) {
      const key = a.pendingActionId ?? a.command;
      const list = dangerous.get(key) ?? [];
      list.push(a);
      dangerous.set(key, list);
    } else {
      ordinary.push(a);
    }
  }

  const dangerCards = Array.from(dangerous.values());

  return (
    <div className="my-3 space-y-3">
      {ordinary.length > 0 && (
        <div className="rounded-lg bg-zinc-900/50 border border-zinc-800 p-3 text-xs">
          <div className="text-zinc-400 font-medium mb-2">Live Action Status</div>
          <ul className="space-y-1.5">
            {ordinary.map((a, i) => (
              <li key={`${a.toolName}-${a.command}-${i}`} className="tool-action flex items-center gap-2 text-zinc-300">
                <span className="w-4 text-center shrink-0">{toolIcon(a)}</span>
                <span className="flex-1 min-w-0 truncate font-mono" title={a.summary}>
                  {a.summary || `${a.toolName} ${a.command}`}
                </span>
                <span className="shrink-0 w-4 text-center">{statusMark(a.status)}</span>
              </li>
            ))}
          </ul>
        </div>
      )}

      {dangerCards.map((events, i) => (
        <DangerousCommandCard key={`danger-${events[0].pendingActionId ?? i}`} events={events} />
      ))}
    </div>
  );
}
