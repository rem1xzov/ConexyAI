import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { getAdminPaymentsSummary } from '../api/conexyApi';
import type { AdminPaymentSummary } from '../types/api';
import { humanError } from '../utils/humanError';
import { copyText } from '../utils/clipboard';

// YOOKASSA: добавлено 2026-09-27
/**
 * Сводка успешных оплат за период. Нужна, чтобы вручную пробить один сводный чек в приложении
 * «Мой налог»: админ выбирает период, копирует итоговую сумму и переносит её в налоговое приложение.
 * Считаются только платежи с проставленной датой оплаты (то есть подтверждённые ЮKassa).
 */

interface AdminPaymentsProps {
  onToast: (message: string) => void;
}

/** Локальная дата в формате YYYY-MM-DD (значение для <input type="date"> и для запроса). */
function toIsoDate(date: Date): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

function shiftDays(date: Date, days: number): Date {
  const result = new Date(date);
  result.setDate(date.getDate() + days);
  return result;
}

/** Понедельник текущей недели — «эта неделя» в русском календаре начинается с него. */
function startOfWeek(date: Date): Date {
  const mondayOffset = (date.getDay() + 6) % 7;
  return shiftDays(date, -mondayOffset);
}

export function AdminPayments({ onToast }: AdminPaymentsProps) {
  const { t, i18n } = useTranslation();
  const lang = i18n.resolvedLanguage ?? i18n.language;

  const today = toIsoDate(new Date());
  const [from, setFrom] = useState(today);
  const [to, setTo] = useState(today);
  const [summary, setSummary] = useState<AdminPaymentSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  // Быстрые «вперёд/назад» по датам не должны показать ответ для предыдущего диапазона.
  const loadSeqRef = useRef(0);

  const load = useCallback(async (rangeFrom: string, rangeTo: string) => {
    const seq = ++loadSeqRef.current;
    setLoading(true);
    setError(null);
    try {
      const result = await getAdminPaymentsSummary(rangeFrom, rangeTo);
      if (seq !== loadSeqRef.current) return;
      setSummary(result);
    } catch (e) {
      if (seq !== loadSeqRef.current) return;
      const status = (e as { response?: { status?: number } })?.response?.status;
      setError(status === 403 ? t('admin.noAccess') : humanError(e, t));
    } finally {
      if (seq === loadSeqRef.current) setLoading(false);
    }
  }, [t]);

  useEffect(() => {
    void load(from, to);
  }, [from, to, load]);

  function applyRange(start: Date, end: Date) {
    setFrom(toIsoDate(start));
    setTo(toIsoDate(end));
  }

  const totalRub = summary?.totalRub ?? 0;
  const items = summary?.payments ?? [];

  function formatMoney(amount: number): string {
    try {
      return new Intl.NumberFormat(lang, {
        style: 'currency',
        currency: 'RUB',
        currencyDisplay: 'narrowSymbol',
        maximumFractionDigits: 0,
      }).format(amount);
    } catch {
      return `${amount} ₽`;
    }
  }

  function formatDateTime(iso: string): string {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return iso;
    return date.toLocaleString(lang === 'en' ? 'en-US' : 'ru-RU', {
      day: '2-digit',
      month: '2-digit',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    });
  }

  async function copyTotal() {
    // Копируем чистое число без пробелов и знака рубля — «Мой налог» принимает его как есть.
    if (!(await copyText(String(totalRub)))) {
      onToast(t('admin.payments.copyFailed'));
      return;
    }
    setCopied(true);
    onToast(t('admin.payments.copied'));
    window.setTimeout(() => setCopied(false), 1500);
  }

  return (
    <div className="admin-payments">
      <div className="admin-payments__filters">
        <label className="admin-payments__field">
          <span className="admin-payments__label">{t('admin.payments.from')}</span>
          <input
            className="admin-input"
            type="date"
            value={from}
            max={to}
            onChange={(e) => setFrom(e.target.value)}
          />
        </label>
        <label className="admin-payments__field">
          <span className="admin-payments__label">{t('admin.payments.to')}</span>
          <input
            className="admin-input"
            type="date"
            value={to}
            min={from}
            onChange={(e) => setTo(e.target.value)}
          />
        </label>
        <div className="admin-payments__quick">
          <button className="admin-btn" type="button" onClick={() => applyRange(new Date(), new Date())}>
            {t('admin.payments.quickToday')}
          </button>
          <button
            className="admin-btn"
            type="button"
            onClick={() => applyRange(shiftDays(new Date(), -1), shiftDays(new Date(), -1))}
          >
            {t('admin.payments.quickYesterday')}
          </button>
          <button className="admin-btn" type="button" onClick={() => applyRange(startOfWeek(new Date()), new Date())}>
            {t('admin.payments.quickWeek')}
          </button>
        </div>
      </div>

      <div className="admin-payments__total">
        <div className="admin-payments__total-block">
          <span className="admin-payments__total-label">{t('admin.payments.total')}</span>
          <span className="admin-payments__total-value">{formatMoney(totalRub)}</span>
          <span className="admin-payments__total-hint">
            {t('admin.payments.count', { count: summary?.count ?? 0 })}
          </span>
        </div>
        <button className="admin-btn" type="button" onClick={() => void copyTotal()} disabled={loading}>
          {copied ? t('admin.payments.copied') : t('admin.payments.copy')}
        </button>
      </div>

      {loading ? (
        <p className="muted admin__empty">{t('common.loading')}</p>
      ) : error ? (
        <div className="admin__error-block">
          <p className="admin__error">{error}</p>
          <div className="admin__error-actions">
            <button className="admin-btn" type="button" onClick={() => void load(from, to)}>
              {t('admin.retry')}
            </button>
          </div>
        </div>
      ) : items.length === 0 ? (
        <p className="muted admin__empty">{t('admin.payments.empty')}</p>
      ) : (
        <div className="admin-payments__table-wrap">
          <table className="admin-payments__table">
            <thead>
              <tr>
                <th>{t('admin.payments.colDate')}</th>
                <th>{t('admin.payments.colAmount')}</th>
                <th>{t('admin.payments.colPlan')}</th>
                <th>{t('admin.payments.colBuyer')}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item) => (
                <tr key={item.paymentId}>
                  <td>{formatDateTime(item.paidAt)}</td>
                  <td className="admin-payments__amount">{formatMoney(item.amountRub)}</td>
                  <td>{item.planId}</td>
                  <td className="admin-payments__buyer">
                    {item.email ?? item.username ?? item.userId}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
