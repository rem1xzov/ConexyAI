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

/** Рендерит запрошенный документ; общий каркас живёт в `LegalLayout`. */
export function LegalPage({ doc, onBack }: LegalPageProps) {
  if (doc === 'offer') return <PublicOffer onBack={onBack} />;
  if (doc === 'refund') return <RefundPolicy onBack={onBack} />;
  return <PrivacyPolicy onBack={onBack} />;
}
