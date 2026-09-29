import { useTranslation } from 'react-i18next';
import type { LimitExceededInfo } from '../types/api';
// LEGAL_DOCS: добавлено 2026-09-25 — акцепт Оферты прямо на экране тарифов.
import { LEGAL_PATHS } from './legal/LegalPage';
import { CheckIcon, CloseIcon } from './Icons';

interface Plan {
  id: string;
  name: string;
  /** Monthly price in rubles; 0 = free. */
  priceRub: number;
  ctaKey: string | null;
  featureKeys: string[];
  /**
   * TIER_GO / COWORK_BUDGET: строка про Cowork. На платных тарифах режим открывается — и тогда
   * показывается только сам факт, БЕЗ числа токенов (числа у Cowork и агента разные, и в карточке
   * они только путали бы). На Free строка есть, но помечена как недоступная.
   */
  coworkKey: string;
  coworkLocked: boolean;
  // ORCHESTRA: оркестр агентов — только на ProMax, поэтому строка есть у каждого тарифа: где-то
  // как доступная возможность, где-то как отсечённая — чтобы разницу между тарифами было видно.
  orchestraLocked: boolean;
  highlight?: boolean;
}

// SUBSCRIPTION_TIERS: изменено 2026-09-26 — четыре тарифа (Free/Go/Pro/ProMax) и годовой Ultra.
// Числа ЗДЕСЬ только для показа: реальные лимиты считает бэкенд по appsettings.json
// (SubscriptionLimits), поэтому при правке лимитов нужно обновить и эти подписи.
const PLANS: Plan[] = [
  {
    id: 'Free',
    name: 'Free',
    priceRub: 0,
    ctaKey: null,
    featureKeys: ['upgrade.features.free1', 'upgrade.features.free2', 'upgrade.features.free3'],
    coworkKey: 'upgrade.features.coworkLocked',
    coworkLocked: true,
    orchestraLocked: true,
  },
  {
    id: 'Go',
    name: 'Go',
    priceRub: 590,
    ctaKey: 'upgrade.buyGo',
    featureKeys: ['upgrade.features.go1', 'upgrade.features.go2', 'upgrade.features.go3'],
    coworkKey: 'upgrade.features.coworkAvailable',
    coworkLocked: false,
    orchestraLocked: true,
  },
  {
    id: 'Pro',
    name: 'Pro',
    priceRub: 990,
    ctaKey: 'upgrade.buyPro',
    highlight: true,
    featureKeys: ['upgrade.features.pro1', 'upgrade.features.pro2', 'upgrade.features.pro3'],
    coworkKey: 'upgrade.features.coworkAvailable',
    coworkLocked: false,
    orchestraLocked: true,
  },
  {
    id: 'ProMax',
    name: 'ProMax',
    priceRub: 1590,
    ctaKey: 'upgrade.buyProMax',
    featureKeys: ['upgrade.features.proMax1', 'upgrade.features.proMax2', 'upgrade.features.proMax3'],
    coworkKey: 'upgrade.features.coworkAvailable',
    coworkLocked: false,
    orchestraLocked: false,
  },
];

// ANNUAL_ULTRA: годовой Ultra — 16 990 ₽ за 12 месяцев вместо 1 590 ₽ × 12 = 19 080 ₽, то есть −11%.
// Процент считаем из цен, а не пишем числом: при правке любой из них подпись «выгоднее» останется правдой.
const ANNUAL_PRICE_RUB = 16_990;
const ANNUAL_MONTHS = 12;

// I18N_FORMAT: добавлено 2026-09-24 — цена форматируется Intl по языку интерфейса («990 ₽» / «₽990»),
// а «/мес» берётся из перевода, а не зашит по-русски.
function formatRub(amount: number, lang: string): string {
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

interface UpgradeModalProps {
  limitInfo: LimitExceededInfo | null;
  onClose: () => void;
  /** Идентификатор плана (Pro/ProMax/…), цены и сумма считаются на сервере. */
  onBuy: (planId: string) => void;
  /** Идёт создание платежа: кнопки блокируем, чтобы не создать два платежа двойным кликом. */
  busy?: boolean;
}

// YOOKASSA: изменено 2026-09-27 — кнопки покупки больше не заглушки: они создают реальный платёж
// (см. App.handleUpgradeBuy) и уводят браузер на страницу оплаты ЮKassa. Сумма берётся из серверного
// каталога планов, поэтому клиент присылает только идентификатор тарифа.
export function UpgradeModal({ limitInfo, onClose, onBuy, busy = false }: UpgradeModalProps) {
  const { t, i18n } = useTranslation();
  const lang = i18n.resolvedLanguage ?? i18n.language;

  const proMax = PLANS[PLANS.length - 1];
  const annualFullPrice = proMax.priceRub * ANNUAL_MONTHS;
  const annualDiscountPct = Math.round((1 - ANNUAL_PRICE_RUB / annualFullPrice) * 100);
  const annualMonthlyPrice = Math.round(ANNUAL_PRICE_RUB / ANNUAL_MONTHS);

  // COWORK_BUDGET: «нужен платный тариф» — не то же самое, что «бюджет кончился», и баннер должен
  // говорить именно это, иначе пользователь пойдёт ждать сброса лимита вместо смены тарифа.
  const banner = limitInfo?.limit === 'cowork_plan' ? t('upgrade.coworkNeedsPlan') : t('upgrade.limitExceeded');

  return (
    <div className="dialog-overlay" role="dialog" aria-modal="true" onMouseDown={onClose}>
      <div className="dialog-card upgrade-modal" onMouseDown={(e) => e.stopPropagation()}>
        <div className="upgrade-modal__head">
          <div className="dialog-title">{t('upgrade.title')}</div>
          <button className="icon-btn" onClick={onClose} aria-label={t('common.close')} type="button">
            <CloseIcon size={18} />
          </button>
        </div>

        {limitInfo && <div className="upgrade-modal__banner">{banner}</div>}

        <div className="upgrade-modal__plans">
          {PLANS.map((plan) => (
            <div key={plan.id} className={`upgrade-plan ${plan.highlight ? 'upgrade-plan--highlight' : ''}`}>
              <div className="upgrade-plan__name">{plan.name}</div>
              <div className="upgrade-plan__price">
                {plan.priceRub > 0
                  ? t('upgrade.perMonth', { price: formatRub(plan.priceRub, lang) })
                  : formatRub(0, lang)}
              </div>
              <ul className="upgrade-plan__features">
                {plan.featureKeys.map((key) => (
                  <li key={key} className="upgrade-plan__feature">
                    <CheckIcon size={14} /> {t(key)}
                  </li>
                ))}
                <li
                  className={`upgrade-plan__feature ${plan.coworkLocked ? 'upgrade-plan__feature--locked' : ''}`}
                >
                  {plan.coworkLocked ? <LockMark /> : <CheckIcon size={14} />} {t(plan.coworkKey)}
                </li>
                <li
                  className={`upgrade-plan__feature ${plan.orchestraLocked ? 'upgrade-plan__feature--locked' : ''}`}
                >
                  {plan.orchestraLocked ? <LockMark /> : <CheckIcon size={14} />}{' '}
                  {t(plan.orchestraLocked ? 'upgrade.features.orchestraLocked' : 'upgrade.features.orchestra')}
                </li>
              </ul>
              {plan.ctaKey ? (
                <button
                  className="dialog-btn dialog-btn--primary upgrade-plan__cta"
                  onClick={() => onBuy(plan.id)}
                  disabled={busy}
                  type="button"
                >
                  {t(plan.ctaKey)}
                </button>
              ) : (
                <div className="upgrade-plan__current">{t('upgrade.currentPlan')}</div>
              )}
            </div>
          ))}
        </div>

        {/* ANNUAL_ULTRA: годовой тариф Ultra — тот же набор, что у ProMax, со скидкой и повышенными
            лимитами. Продаётся только на год; набор и поток оплаты как у прежнего годового ProMax. */}
        <div className="upgrade-annual">
          <div className="upgrade-annual__head">
            <div className="upgrade-annual__title">{t('upgrade.annualTitle')}</div>
            <span className="upgrade-annual__badge">{t('upgrade.annualBadge', { percent: annualDiscountPct })}</span>
            {/* ANNUAL_ULTRA: плашка о том, что у Ultra лимиты выше, чем у ProMax. Конкретные числа
                здесь НЕ приводим — они живут в конфиге тарифов. */}
            <span className="upgrade-annual__limits">{t('upgrade.annualUltraLimits')}</span>
          </div>
          <div className="upgrade-annual__prices">
            <span className="upgrade-annual__price">{t('upgrade.perYear', { price: formatRub(ANNUAL_PRICE_RUB, lang) })}</span>
            <span className="upgrade-annual__was">{formatRub(annualFullPrice, lang)}</span>
            <span className="upgrade-annual__monthly">
              {t('upgrade.annualMonthly', { price: formatRub(annualMonthlyPrice, lang) })}
            </span>
          </div>
          <div className="upgrade-annual__note">
            {t('upgrade.annualSave', {
              full: formatRub(annualFullPrice, lang),
              save: formatRub(annualFullPrice - ANNUAL_PRICE_RUB, lang),
            })}
          </div>
          <ul className="upgrade-plan__features">
            <li className="upgrade-plan__feature">
              <CheckIcon size={14} /> {t('upgrade.annualIncludes')}
            </li>
            <li className="upgrade-plan__feature">
              <CheckIcon size={14} /> {t('upgrade.features.orchestra')}
            </li>
            <li className="upgrade-plan__feature">
              <CheckIcon size={14} /> {t('upgrade.features.coworkAvailable')}
            </li>
          </ul>
          <button
            className="dialog-btn dialog-btn--primary upgrade-plan__cta"
            onClick={() => onBuy('ProMaxAnnual')}
            disabled={busy}
            type="button"
          >
            {t('upgrade.buyAnnual')}
          </button>
        </div>

        {/* LEGAL_DOCS: добавлено 2026-09-25 — оплата Тарифа является акцептом Оферты, поэтому
            документы доступны прямо с экрана тарифов. Ссылки открываются в новой вкладке. */}
        <div className="legal-links">
          <p className="legal-links__note">{t('legal.acceptNote')}</p>
          <div className="legal-links__row">
            <a href={LEGAL_PATHS.offer} target="_blank" rel="noopener noreferrer">
              {t('legal.offerLink')}
            </a>
            <span aria-hidden="true">·</span>
            <a href={LEGAL_PATHS.refund} target="_blank" rel="noopener noreferrer">
              {t('legal.refundTitle')}
            </a>
            <span aria-hidden="true">·</span>
            <a href={LEGAL_PATHS.privacy} target="_blank" rel="noopener noreferrer">
              {t('legal.privacyTitle')}
            </a>
          </div>
        </div>
      </div>
    </div>
  );
}

/** Значок «не входит в тариф» — маленький прочерк вместо галочки, чтобы строку нельзя было
 *  принять за включённую возможность. */
function LockMark() {
  return (
    <svg width={14} height={14} viewBox="0 0 24 24" aria-hidden="true" className="upgrade-plan__lock">
      <path d="M5 12h14" stroke="currentColor" strokeWidth={2} strokeLinecap="round" fill="none" />
    </svg>
  );
}
