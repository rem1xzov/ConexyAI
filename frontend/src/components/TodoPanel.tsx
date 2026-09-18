import type { TodoItem } from '../types/signalr';

function statusMark(status: TodoItem['status']): JSX.Element {
  switch (status) {
    case 'in_progress':
      return <span className="text-amber-400">🔄</span>;
    case 'completed':
      return <span className="text-emerald-400">✅</span>;
    case 'skipped':
      return <span className="text-zinc-500">⏭️</span>;
    default:
      return <span className="text-zinc-500">⬜</span>;
  }
}

interface TodoPanelProps {
  todos: TodoItem[];
}

export function TodoPanel({ todos }: TodoPanelProps) {
  if (todos.length === 0) return null;

  return (
    <div className="my-3 rounded-lg bg-zinc-900/50 border border-zinc-800 p-3 text-xs">
      <div className="text-zinc-400 font-medium mb-2">Task Plan</div>
      <ul className="space-y-1.5">
        {todos.map((t) => (
          <li key={t.id} className="flex items-start gap-2 text-zinc-300">
            <span className="shrink-0 w-5 text-center">{statusMark(t.status)}</span>
            <span
              className={`flex-1 min-w-0 ${
                t.status === 'completed' ? 'line-through text-zinc-500' : ''
              } ${t.status === 'skipped' ? 'text-zinc-500' : ''}`}
              title={t.status === 'skipped' ? t.skip_reason : undefined}
            >
              {t.content}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}
