import { useTranslation } from 'react-i18next';
import { useOperatorProfile } from '../../hooks/useOperatorProfile';
import { PrivacyPolicy } from './PrivacyPolicy';
import { PublicOffer } from './PublicOffer';
import { RefundPolicy } from './RefundPolicy';

// LEGAL_DOCS: добавлено 2026-09-25
/**
 * Три правовых документа Сервиса, доступные по прямым адресам. Так как адреса хешевые, каждый
 * документ можно открыть в новой вкладке, не теряя введённые данные и не сбрасывая состояние
 * модалок (тарифы, настройки, регистрация).
 */
export type LegalDoc = 'privacy' | 'offer' | 'refund';

/** Адрес каждого документа — единственное место, где он объявлен; ссылки в UI ведут на `#/…`. */
export const LEGAL_PATHS: Record<LegalDoc, string> = {
  privacy: '#/privacy',
  offer: '#/offer',
  refund: '#/refund',
};

/** Разбирает текущий хеш маршрута в документ; `null`, если открыт не правовой раздел. */
export function legalDocFromHash(hash: string): LegalDoc | null {
  if (hash.startsWith(LEGAL_PATHS.privacy)) return 'privacy';
  if (hash.startsWith(LEGAL_PATHS.offer)) return 'offer';
  if (hash.startsWith(LEGAL_PATHS.refund)) return 'refund';
  return null;
}

interface LegalPageProps {
  doc: LegalDoc;
  onBack: () => void;
}

/**
 * Рендерит запрошенный документ; общий каркас живёт в `LegalLayout`.
 *
 * Реквизиты оператора тянутся с сервера, а не лежат в бандле, поэтому документ показывается только
 * когда они приехали: без них в тексте остались бы пустые места там, где должны быть ФИО, ИНН и
 * адрес. Ошибка загрузки не оставляет пустой экран — есть «Повторить» и выход назад.
 */
export function LegalPage({ doc, onBack }: LegalPageProps) {
  const { t } = useTranslation();
  const { operator, failed, reload } = useOperatorProfile();

  if (failed) {
    return (
      <div className="route-fallback">
        <p className="route-fallback__text">{t('legal.loadError')}</p>
        <button className="admin-btn" type="button" onClick={reload}>
          {t('sync.retry')}
        </button>
        <button className="admin-btn" type="button" onClick={onBack}>
          {t('common.back')}
        </button>
      </div>
    );
  }

  if (!operator) {
    return (
      <div className="route-fallback">
        <span className="route-fallback__spinner" aria-hidden="true" />
        <p className="route-fallback__text">{t('common.loading')}</p>
        <button className="admin-btn" type="button" onClick={onBack}>
          {t('common.back')}
        </button>
      </div>
    );
  }

  if (doc === 'offer') return <PublicOffer operator={operator} onBack={onBack} />;
  if (doc === 'refund') return <RefundPolicy operator={operator} onBack={onBack} />;
  return <PrivacyPolicy operator={operator} onBack={onBack} />;
}
