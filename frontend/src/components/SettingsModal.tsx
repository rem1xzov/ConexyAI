import { useTranslation } from 'react-i18next';
import type { Theme } from '../theme';
import { CloseIcon } from './Icons';

interface SettingsModalProps {
  theme: Theme;
  language: string;
  onThemeChange: (theme: Theme) => void;
  onLanguageChange: (lang: string) => void;
  onClose: () => void;
}

const THEMES: { value: Theme; key: string }[] = [
  { value: 'light', key: 'settings.themeLight' },
  { value: 'dark', key: 'settings.themeDark' },
  { value: 'system', key: 'settings.themeSystem' },
];

const LANGS: { value: string; key: string }[] = [
  { value: 'ru', key: 'settings.langRu' },
  { value: 'en', key: 'settings.langEn' },
];

// SETTINGS: добавлено 2026-09-19
/** Client-only settings modal: theme (light/dark/system) and language (ru/en). */
export function SettingsModal({ theme, language, onThemeChange, onLanguageChange, onClose }: SettingsModalProps) {
  const { t } = useTranslation();

  return (
    <div className="dialog-overlay" onMouseDown={onClose}>
      <div className="dialog-card settings-modal" role="dialog" aria-modal="true" onMouseDown={(e) => e.stopPropagation()}>
        <div className="settings-modal__head">
          <div className="dialog-title">{t('settings.title')}</div>
          <button className="icon-btn" onClick={onClose} aria-label={t('common.close')} type="button">
            <CloseIcon size={18} />
          </button>
        </div>

        <div className="settings-modal__section">
          <div className="settings-modal__label">{t('settings.theme')}</div>
          <div className="settings-modal__segmented">
            {THEMES.map((th) => (
              <button
                key={th.value}
                className={`settings-seg ${theme === th.value ? 'settings-seg--active' : ''}`}
                onClick={() => onThemeChange(th.value)}
                type="button"
              >
                {t(th.key)}
              </button>
            ))}
          </div>
        </div>

        <div className="settings-modal__section">
          <div className="settings-modal__label">{t('settings.language')}</div>
          <div className="settings-modal__segmented">
            {LANGS.map((l) => (
              <button
                key={l.value}
                className={`settings-seg ${language === l.value ? 'settings-seg--active' : ''}`}
                onClick={() => onLanguageChange(l.value)}
                type="button"
              >
                {t(l.key)}
              </button>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
