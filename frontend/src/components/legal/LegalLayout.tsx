import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { ConexyLogo } from '../ConexyLogo';

// LEGAL_DOCS: добавлено 2026-09-25
/**
 * Общий каркас правового документа: шапка с возвратом, колонка текста, даты и оговорка о языке.
 *
 * Документы доступны и гостю, и авторизованному пользователю по прямым адресам (`#/privacy`,
 * `#/offer`, `#/refund`), поэтому их можно открывать в новой вкладке из настроек, из формы
 * регистрации и с экрана тарифов, не теряя введённые данные.
 */
interface LegalLayoutProps {
  title: string;
  /** Показывается только у Публичной оферты: у остальных документов дата публикации не нужна. */
  publishedAt?: string;
  updatedAt: string;
  onBack: () => void;
  children: ReactNode;
}

export function LegalLayout({ title, publishedAt, updatedAt, onBack, children }: LegalLayoutProps) {
  const { t, i18n } = useTranslation();
  // Тексты документов юридически значимы только на русском, поэтому английская локаль получает
  // предупреждение о том, что применению подлежит русская редакция.
  const isRussian = i18n.language.startsWith('ru');

  return (
    <div className="legal-page">
      <header className="legal-page__bar">
        <button className="legal-page__back" type="button" onClick={onBack}>
          {t('common.back')}
        </button>
        <span className="legal-page__brand" aria-hidden="true">
          <ConexyLogo size={24} />
        </span>
      </header>

      <main className="legal-page__doc">
        <h1 className="legal-page__title">{title}</h1>
        <p className="legal-page__meta">
          {publishedAt && (
            <>
              {t('legal.published', { date: publishedAt })}
              <br />
            </>
          )}
          {t('legal.updated', { date: updatedAt })}
        </p>

        {!isRussian && <p className="legal-page__notice">{t('legal.englishNotice')}</p>}

        {children}
      </main>
    </div>
  );
}
