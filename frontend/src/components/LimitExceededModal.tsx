import type { LimitExceededInfo } from '../types/api';

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
interface LimitExceededModalProps {
  info: LimitExceededInfo;
  onClose: () => void;
}

const LIMIT_LABELS: Record<string, string> = {
  flash: 'ConexyV1-flash',
  pro: 'ConexyV1-pro',
  agent: 'Agent (conexy-coder)',
};

function formatDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric' });
}

/**
 * Stub modal shown when a subscription limit is exhausted. The upgrade flow is not
 * implemented yet — "Оформить подписку" is a placeholder.
 */
export function LimitExceededModal({ info, onClose }: LimitExceededModalProps) {
  const label = LIMIT_LABELS[info.limit] ?? info.limit;

  return (
    <div className="dialog-overlay" role="dialog" aria-modal="true" onMouseDown={onClose}>
      <div className="dialog-card" onMouseDown={(e) => e.stopPropagation()}>
        <div className="dialog-title">Лимит исчерпан</div>
        <div className="dialog-message">
          Вы достигли лимита для <strong>{label}</strong>.
          <br />
          Следующий сброс: {formatDate(info.resetsAt)}.
        </div>
        <div className="dialog-actions">
          <button className="dialog-btn" onClick={onClose} type="button">
            Закрыть
          </button>
          <button className="dialog-btn dialog-btn--primary" onClick={onClose} type="button">
            Оформить подписку
          </button>
        </div>
      </div>
    </div>
  );
}
