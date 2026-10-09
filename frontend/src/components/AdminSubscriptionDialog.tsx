import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { AdminUser } from '../types/api';
import { DialogShell } from './Dialog';

// ADMIN_SUBSCRIPTION: добавлено 2026-10-09 — админ назначает пользователю тариф вручную.
// Тариф Admin здесь недоступен: он выдаётся действием «сделать админом».
const ASSIGNABLE_TIERS = ['Free', 'Go', 'Pro', 'ProMax', 'Ultra'] as const;

// INDEFINITE = null: ручная выдача без срока (SubscriptionExpiresAt = null).
const DURATIONS: { value: number | null; key: string }[] = [
  { value: 1, key: 'm1' },
  { value: 3, key: 'm3' },
  { value: 6, key: 'm6' },
  { value: 12, key: 'm12' },
  { value: null, key: 'indefinite' },
];

interface AdminSubscriptionDialogProps {
  user: AdminUser;
  busy?: boolean;
  onSubmit: (tier: string, months: number | null) => void;
  onCancel: () => void;
}

/** Admin-only dialog to assign a subscription tier (and optional duration) to a user. */
export function AdminSubscriptionDialog({ user, busy, onSubmit, onCancel }: AdminSubscriptionDialogProps) {
  const { t } = useTranslation();
  const titleId = useId();
  const defaultTier = (ASSIGNABLE_TIERS as readonly string[]).includes(user.tier) ? user.tier : 'Pro';
  const [tier, setTier] = useState<string>(defaultTier);
  const [months, setMonths] = useState<number | null>(1);

  return (
    <DialogShell onCancel={busy ? () => {} : onCancel} labelledBy={titleId}>
      <div className="dialog-title" id={titleId}>
        {t('admin.sub.title')}
      </div>
      <div className="dialog-message">{t('admin.sub.hint')}</div>

      <label className="dialog-field">
        <span className="dialog-field__label">{t('admin.sub.tierLabel')}</span>
        <select
          className="dialog-input"
          value={tier}
          onChange={(e) => setTier(e.target.value)}
          disabled={busy}
        >
          {ASSIGNABLE_TIERS.map((value) => (
            <option key={value} value={value}>
              {value}
            </option>
          ))}
        </select>
      </label>

      <label className="dialog-field">
        <span className="dialog-field__label">{t('admin.sub.durationLabel')}</span>
        <select
          className="dialog-input"
          value={months === null ? 'indefinite' : String(months)}
          onChange={(e) => setMonths(e.target.value === 'indefinite' ? null : Number(e.target.value))}
          disabled={busy}
        >
          {DURATIONS.map((d) => (
            <option key={d.key} value={d.value === null ? 'indefinite' : String(d.value)}>
              {d.value === null ? t('admin.sub.indefinite') : t('admin.sub.months', { count: d.value })}
            </option>
          ))}
        </select>
      </label>

      <div className="dialog-actions">
        <button className="dialog-btn" onClick={onCancel} type="button" disabled={busy}>
          {t('common.cancel')}
        </button>
        <button
          className="dialog-btn dialog-btn--primary"
          onClick={() => onSubmit(tier, months)}
          type="button"
          disabled={busy}
        >
          {t('admin.sub.apply')}
        </button>
      </div>
    </DialogShell>
  );
}
