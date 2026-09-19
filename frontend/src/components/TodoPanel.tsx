import { useTranslation } from 'react-i18next';
import type { TodoItem } from '../types/signalr';

function statusMark(status: TodoItem['status']): JSX.Element {
  switch (status) {
    case 'in_progress':
      return <span className="chat-warn">🔄</span>;
    case 'completed':
      return <span className="chat-ok">✅</span>;
    case 'skipped':
      return <span className="chat-muted">⏭️</span>;
    default:
      return <span className="chat-muted">⬜</span>;
  }
}

interface TodoPanelProps {
  todos: TodoItem[];
}

export function TodoPanel({ todos }: TodoPanelProps) {
  const { t } = useTranslation();
  if (todos.length === 0) return null;

  return (
    <div className="my-3 rounded-lg chat-surface-soft p-3 text-xs">
      <div className="chat-muted font-medium mb-2">{t('todo.plan')}</div>
      <ul className="space-y-1.5">
        {todos.map((t) => (
          <li key={t.id} className="flex items-start gap-2 chat-text">
            <span className="shrink-0 w-5 text-center">{statusMark(t.status)}</span>
            <span
              className={`flex-1 min-w-0 ${
                t.status === 'completed' ? 'line-through chat-muted' : ''
              } ${t.status === 'skipped' ? 'chat-muted' : ''}`}
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
