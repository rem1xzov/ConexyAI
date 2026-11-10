import { useTranslation } from 'react-i18next';

// VERSIONS/BRANCHES: компактный пагинатор `< X / Y >`. Показывается только когда вариантов больше
// одного; крайние стрелки гаснут, как в референсе.
interface VariantPagerProps {
  /** 1-based index of the active variant. */
  current: number;
  total: number;
  onChange: (zeroBasedIndex: number) => void;
  className?: string;
}

export function VariantPager({ current, total, onChange, className }: VariantPagerProps) {
  const { t } = useTranslation();
  if (total <= 1) return null;
  const active = Math.min(Math.max(current, 1), total);

  return (
    <div className={`variant-pager${className ? ` ${className}` : ''}`}>
      <button
        type="button"
        className="variant-pager__btn"
        disabled={active <= 1}
        onClick={() => onChange(active - 2)}
        aria-label={t('message.previousVariant')}
        title={t('message.previousVariant')}
      >
        ‹
      </button>
      <span className="variant-pager__label">
        {active} / {total}
      </span>
      <button
        type="button"
        className="variant-pager__btn"
        disabled={active >= total}
        onClick={() => onChange(active)}
        aria-label={t('message.nextVariant')}
        title={t('message.nextVariant')}
      >
        ›
      </button>
    </div>
  );
}
