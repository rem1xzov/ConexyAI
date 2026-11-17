import { useTranslation } from 'react-i18next';
import { ConexyLogo } from './ConexyLogo';
import type { LimitPoolKey } from '../utils/limitPools';

// LIMIT_LOCK: добавлено 2026-11-17 — экран «лимит закончился». Показывается вместо чата, когда пул
// текущего режима (Flash, Pro, Coder или Cowork) выбран полностью, с фирменным логотипом и
// локализованным текстом. Плашка с датой сброса живёт над композером — как в референсе.
const POOL_LABEL_KEY: Record<LimitPoolKey, string> = {
  flash: 'limitLock.pool.flash',
  pro: 'limitLock.pool.pro',
  coder: 'limitLock.pool.coder',
  cowork: 'limitLock.pool.cowork',
};

function splitReset(iso: string, lang: string): { date: string; time: string } | null {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  const locale = lang === 'en' ? 'en-US' : 'ru-RU';
  return {
    date: d.toLocaleDateString(locale, { day: '2-digit', month: '2-digit', year: 'numeric' }),
    time: d.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' }),
  };
}

/** Центральная часть заблокированного экрана: логотип и приветствие. */
export function LimitLockHero() {
  const { t } = useTranslation();
  return (
    <div className="limit-lock">
      <ConexyLogo size={92} />
      <p className="limit-lock__greeting">{t('limitLock.greeting')}</p>
    </div>
  );
}

interface LimitNoticeProps {
  /** Пул, который выбран до конца; null — сервер отказал, но расход ещё не перечитан. */
  pool: LimitPoolKey | null;
  /** Когда лимит восстановится (ISO). Пустая строка — дата неизвестна. */
  resetsAt: string;
  /** `cowork_plan`: режим не входит в тариф — это не исчерпанный лимит, а предложение тарифа. */
  planRequired?: boolean;
  onUpgrade: () => void;
}

/** Плашка над полем ввода: что произошло и когда лимит вернётся. */
export function LimitNotice({ pool, resetsAt, planRequired = false, onUpgrade }: LimitNoticeProps) {
  const { t, i18n } = useTranslation();
  const lang = i18n.resolvedLanguage ?? i18n.language;
  const reset = planRequired ? null : splitReset(resetsAt, lang);

  return (
    <div className="limit-notice" role="status">
      <div className="limit-notice__head">
        <span className="limit-notice__dot" aria-hidden="true" />
        <span className="limit-notice__title">
          {planRequired ? t('limitLock.planTitle') : `${t('limitLock.serverBusy')} ${t('limitLock.limitReached')}`}
        </span>
        {pool && <span className="limit-notice__pool">{t(POOL_LABEL_KEY[pool])}</span>}
      </div>
      <p className="limit-notice__text">
        {planRequired
          ? t('limitLock.planRequired')
          : reset
            ? t('limitLock.resetAt', reset)
            : t('limitLock.resetUnknown')}
      </p>
      <button className="limit-notice__btn" type="button" onClick={onUpgrade}>
        {t('limitLock.upgrade')}
      </button>
    </div>
  );
}
