import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { getPaymentStatus } from '../api/paymentsApi';
import type { PaymentStatusResponse } from '../types/api';
import { CheckIcon, CloseIcon } from './Icons';

// YOOKASSA: добавлено 2026-09-27
/**
 * Окно после возврата с оплаты. ЮKassa переводит браузер на return_url сразу после оплаты, но
 * вебхук и запись тарифа могут отставать на секунды, поэтому статус опрашивается, пока не станет
 * окончательным (или пока не выйдет время ожидания).
 */
type Phase = 'checking' | 'succeeded' | 'canceled' | 'timeout';

const POLL_INTERVAL_MS = 2500;
const POLL_TIMEOUT_MS = 90_000;

interface PaymentReturnModalProps {
  paymentId: string;
  onClose: () => void;
  /** Вызывается один раз при подтверждении оплаты — чтобы обновить лимиты в интерфейсе. */
  onPaid: () => void;
}

export function PaymentReturnModal({ paymentId, onClose, onPaid }: PaymentReturnModalProps) {
  const { t } = useTranslation();
  const [phase, setPhase] = useState<Phase>('checking');
  const [status, setStatus] = useState<PaymentStatusResponse | null>(null);
  const [attempt, setAttempt] = useState(0);

  const paidRef = useRef(false);
  const deadlineRef = useRef(0);

  const check = useCallback(async (): Promise<boolean> => {
    try {
      const result = await getPaymentStatus(paymentId);
      setStatus(result);
      if (result.status === 'succeeded') {
        setPhase('succeeded');
        if (!paidRef.current) {
          paidRef.current = true;
          onPaid();
        }
        return true;
      }
      if (result.status === 'canceled') {
        setPhase('canceled');
        return true;
      }
    } catch {
      // Сеть/сервер могут моргнуть — не сдаёмся сразу, покажем «ещё обрабатывается» по таймауту.
    }
    return false;
  }, [paymentId, onPaid]);

  useEffect(() => {
    let timer: number | null = null;
    let stopped = false;
    deadlineRef.current = Date.now() + POLL_TIMEOUT_MS;

    async function tick() {
      const done = await check();
      if (stopped || done) return;
      if (Date.now() > deadlineRef.current) {
        setPhase('timeout');
        return;
      }
      timer = window.setTimeout(tick, POLL_INTERVAL_MS);
    }

    void tick();
    return () => {
      stopped = true;
      if (timer !== null) window.clearTimeout(timer);
    };
  }, [check, attempt]);

  function retry() {
    setPhase('checking');
    setAttempt((value) => value + 1);
  }

  return (
    <div className="dialog-overlay" role="dialog" aria-modal="true" onMouseDown={onClose}>
      <div className="dialog-card payment-modal" onMouseDown={(e) => e.stopPropagation()}>
        <div className="upgrade-modal__head">
          <div className="dialog-title">{t('payment.title')}</div>
          <button className="icon-btn" onClick={onClose} aria-label={t('common.close')} type="button">
            <CloseIcon size={18} />
          </button>
        </div>

        {phase === 'checking' && (
          <div className="payment-modal__body">
            <span className="payment-modal__spinner" aria-hidden="true" />
            <p className="payment-modal__text">{t('payment.checking')}</p>
            <p className="payment-modal__hint">{t('payment.checkingHint')}</p>
          </div>
        )}

        {phase === 'succeeded' && (
          <div className="payment-modal__body">
            <span className="payment-modal__icon payment-modal__icon--ok" aria-hidden="true">
              <CheckIcon size={22} />
            </span>
            <p className="payment-modal__text">{t('payment.success')}</p>
            <p className="payment-modal__hint">
              {status?.tier ? t('payment.successHint', { tier: status.tier }) : t('payment.successHintPlain')}
            </p>
            <button className="dialog-btn dialog-btn--primary" onClick={onClose} type="button">
              {t('payment.close')}
            </button>
          </div>
        )}

        {phase === 'canceled' && (
          <div className="payment-modal__body">
            <p className="payment-modal__text payment-modal__text--bad">{t('payment.canceled')}</p>
            <p className="payment-modal__hint">{t('payment.canceledHint')}</p>
            <button className="dialog-btn dialog-btn--primary" onClick={onClose} type="button">
              {t('payment.close')}
            </button>
          </div>
        )}

        {phase === 'timeout' && (
          <div className="payment-modal__body">
            <p className="payment-modal__text">{t('payment.timeout')}</p>
            <p className="payment-modal__hint">{t('payment.timeoutHint')}</p>
            <button className="dialog-btn dialog-btn--primary" onClick={retry} type="button">
              {t('payment.retry')}
            </button>
          </div>
        )}
      </div>
    </div>
  );
}
