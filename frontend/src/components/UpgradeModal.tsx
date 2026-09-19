import type { LimitExceededInfo } from '../types/api';
import { CheckIcon, CloseIcon } from './Icons';

interface Plan {
  id: string;
  name: string;
  price: string;
  cta: string | null;
  features: string[];
  highlight?: boolean;
}

const PLANS: Plan[] = [
  {
    id: 'Free',
    name: 'Free',
    price: '0 ₽',
    cta: null,
    features: [
      '100 запросов Flash/неделя',
      '20 запросов Pro/неделя',
      '200 000 токенов агента/месяц',
    ],
  },
  {
    id: 'Pro',
    name: 'Pro',
    price: '990 ₽/мес',
    cta: 'Купить Pro',
    highlight: true,
    features: [
      '250 запросов Flash/неделя',
      '150 запросов Pro/неделя',
      '5 000 000 токенов агента/неделя',
    ],
  },
  {
    id: 'ProMax',
    name: 'ProMax',
    price: '1 590 ₽/мес',
    cta: 'Купить ProMax',
    features: [
      '300 запросов Flash/неделя',
      '200 запросов Pro/неделя',
      '10 000 000 токенов агента/неделя',
    ],
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
  return (
    <div className="dialog-overlay" role="dialog" aria-modal="true" onMouseDown={onClose}>
      <div className="dialog-card upgrade-modal" onMouseDown={(e) => e.stopPropagation()}>
        <div className="upgrade-modal__head">
          <div className="dialog-title">Улучшите тариф</div>
          <button className="icon-btn" onClick={onClose} aria-label="Закрыть" type="button">
            <CloseIcon size={18} />
          </button>
        </div>

        {limitInfo && (
          <div className="upgrade-modal__banner">
            Вы достигли лимита. Улучшите тариф, чтобы продолжить.
          </div>
        )}

        <div className="upgrade-modal__plans">
          {PLANS.map((plan) => (
            <div key={plan.id} className={`upgrade-plan ${plan.highlight ? 'upgrade-plan--highlight' : ''}`}>
              <div className="upgrade-plan__name">{plan.name}</div>
              <div className="upgrade-plan__price">{plan.price}</div>
              <ul className="upgrade-plan__features">
                {plan.features.map((f) => (
                  <li key={f} className="upgrade-plan__feature">
                    <CheckIcon size={14} /> {f}
                  </li>
                ))}
              </ul>
              {plan.cta ? (
                <button className="dialog-btn dialog-btn--primary upgrade-plan__cta" onClick={() => onBuy(plan.name)} type="button">
                  {plan.cta}
                </button>
              ) : (
                <div className="upgrade-plan__current">Текущий план</div>
              )}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
