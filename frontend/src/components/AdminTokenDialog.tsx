import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { AdminUser, TokenPool } from '../types/api';
import { DialogShell } from './Dialog';

// ADMIN_TOKEN_TOPUP: купленные токены начисляются админом вручную в конкретный пул.
// Сумма — целое положительное число; бэкенд отвечает 400 на всё остальное, поэтому проверяем здесь.
const POOLS: TokenPool[] = ['Coder', 'Cowork'];
const QUICK_AMOUNTS = [100_000, 1_000_000, 5_000_000];

/** "100 тыс." / "1 млн" — компактная подпись для кнопок быстрого выбора и сумм в строке списка. */
export function formatTokenAmount(value: number, lang: string): string {
  try {
    return new Intl.NumberFormat(lang, { notation: 'compact', maximumFractionDigits: 1 }).format(value);
  } catch {
    return String(value);
  }
}

interface AdminTokenDialogProps {
  user: AdminUser;
  busy?: boolean;
  onSubmit: (pool: TokenPool, amount: number, reason: string) => void;
  onCancel: () => void;
}

/** Admin-only dialog to credit purchased tokens to a user's Coder or Cowork pool. */
export function AdminTokenDialog({ user, busy, onSubmit, onCancel }: AdminTokenDialogProps) {
  const { t, i18n } = useTranslation();
  const lang = i18n.resolvedLanguage ?? i18n.language;
  const titleId = useId();
  const [pool, setPool] = useState<TokenPool>('Coder');
  const [amount, setAmount] = useState('');
  const [reason, setReason] = useState('');

  const trimmed = amount.trim();
  const parsed = Number(trimmed);
  // Регулярное выражение отсекает "1.5", "1e6" и пустую строку, которые Number() пропустил бы.
  const valid = /^\d+$/.test(trimmed) && Number.isSafeInteger(parsed) && parsed > 0;
  const showInvalid = trimmed.length > 0 && !valid;
  const coderTopUp = user.coderTokenTopUp ?? 0;
  const coworkTopUp = user.coworkTokenTopUp ?? 0;

  function submit() {
    if (!valid || busy) return;
    onSubmit(pool, parsed, reason.trim());
  }

  return (
    <DialogShell onCancel={busy ? () => {} : onCancel} labelledBy={titleId}>
      <div className="dialog-title" id={titleId}>
        {t('admin.tokens.title')}
      </div>
      <div className="dialog-message">{t('admin.tokens.hint')}</div>
      <div className="dialog-hint">
        {t('admin.tokens.current', {
          coder: formatTokenAmount(coderTopUp, lang),
          cowork: formatTokenAmount(coworkTopUp, lang),
        })}
      </div>

      <label className="dialog-field">
        <span className="dialog-field__label">{t('admin.tokens.poolLabel')}</span>
        <select
          className="dialog-input"
          value={pool}
          onChange={(e) => setPool(e.target.value as TokenPool)}
          disabled={busy}
        >
          {POOLS.map((value) => (
            <option key={value} value={value}>
              {value}
            </option>
          ))}
        </select>
      </label>

      <label className="dialog-field">
        <span className="dialog-field__label">{t('admin.tokens.amountLabel')}</span>
        <input
          className="dialog-input"
          type="number"
          inputMode="numeric"
          min={1}
          step={1}
          value={amount}
          placeholder={t('admin.tokens.amountPlaceholder')}
          onChange={(e) => setAmount(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') submit();
          }}
          disabled={busy}
        />
        <div className="dialog-quick">
          {QUICK_AMOUNTS.map((value) => (
            <button
              key={value}
              className="admin-btn"
              type="button"
              onClick={() => setAmount(String(value))}
              disabled={busy}
            >
              {formatTokenAmount(value, lang)}
            </button>
          ))}
        </div>
        {showInvalid && <span className="dialog-error">{t('admin.tokens.invalidAmount')}</span>}
      </label>

      <label className="dialog-field">
        <span className="dialog-field__label">{t('admin.tokens.reasonLabel')}</span>
        <input
          className="dialog-input"
          type="text"
          value={reason}
          placeholder={t('admin.tokens.reasonPlaceholder')}
          maxLength={500}
          onChange={(e) => setReason(e.target.value)}
          disabled={busy}
        />
      </label>

      <div className="dialog-actions">
        <button className="dialog-btn" onClick={onCancel} type="button" disabled={busy}>
          {t('common.cancel')}
        </button>
        <button
          className="dialog-btn dialog-btn--primary"
          onClick={submit}
          type="button"
          disabled={busy || !valid}
        >
          {t('admin.tokens.apply')}
        </button>
      </div>
    </DialogShell>
  );
}
