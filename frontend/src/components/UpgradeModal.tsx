import { useTranslation } from 'react-i18next';
import type { LimitExceededInfo } from '../types/api';
import { CheckIcon, CloseIcon } from './Icons';

interface Plan {
  id: string;
  name: string;
  price: string;
  ctaKey: string | null;
  featureKeys: string[];
  highlight?: boolean;
}

const PLANS: Plan[] = [
  {
    id: 'Free',
    name: 'Free',
    price: '0 ₽',
    ctaKey: null,
    featureKeys: ['upgrade.features.free1', 'upgrade.features.free2', 'upgrade.features.free3'],
  },
  {
    id: 'Pro',
    name: 'Pro',
    price: '990 ₽/мес',
    ctaKey: 'upgrade.buyPro',
    highlight: true,
    featureKeys: ['upgrade.features.pro1', 'upgrade.features.pro2', 'upgrade.features.pro3'],
  },
  {
    id: 'ProMax',
    name: 'ProMax',
    price: '1 590 ₽/мес',
    ctaKey: 'upgrade.buyProMax',
    featureKeys: ['upgrade.features.proMax1', 'upgrade.features.proMax2', 'upgrade.features.proMax3'],
  },
];

interface UpgradeModalProps {
  limitInfo: LimitExceededInfo | null;
  onClose: () => void;
  onBuy: (planName: string) => void;
}

// ADMIN_PANEL: добавлено 2026-09-19
/**
 * Full upgrade modal with three plan cards (Free / Pro / ProMax). Shown when a limit is
 * exceeded or when the user clicks "Улучшить план" in the account menu. "Buy" buttons are
 * stubs until real payment is wired up.
 */
export function UpgradeModal({ limitInfo, onClose, onBuy }: UpgradeModalProps) {
  const { t } = useTranslation();

  return (
    <div className="dialog-overlay" role="dialog" aria-modal="true" onMouseDown={onClose}>
      <div className="dialog-card upgrade-modal" onMouseDown={(e) => e.stopPropagation()}>
        <div className="upgrade-modal__head">
          <div className="dialog-title">{t('upgrade.title')}</div>
          <button className="icon-btn" onClick={onClose} aria-label={t('common.close')} type="button">
            <CloseIcon size={18} />
          </button>
        </div>

        {limitInfo && <div className="upgrade-modal__banner">{t('upgrade.limitExceeded')}</div>}

        <div className="upgrade-modal__plans">
          {PLANS.map((plan) => (
            <div key={plan.id} className={`upgrade-plan ${plan.highlight ? 'upgrade-plan--highlight' : ''}`}>
              <div className="upgrade-plan__name">{plan.name}</div>
              <div className="upgrade-plan__price">{plan.price}</div>
              <ul className="upgrade-plan__features">
                {plan.featureKeys.map((key) => (
                  <li key={key} className="upgrade-plan__feature">
                    <CheckIcon size={14} /> {t(key)}
                  </li>
                ))}
              </ul>
              {plan.ctaKey ? (
                <button
                  className="dialog-btn dialog-btn--primary upgrade-plan__cta"
                  onClick={() => onBuy(plan.name)}
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
      </div>
    </div>
  );
}
