import { useTranslation } from 'react-i18next';
import { ConexyLogo } from './ConexyLogo';

// GUEST_HERO: добавлено 2026-09-20
/**
 * Conversion block shown on the start screen instead of the personalised greeting when nobody
 * is signed in. It replaces the greeting inside the chat stage hero, so the auth buttons sit
 * centred above the composer on every viewport.
 *
 * The pitch follows the active tab, so the guest sees what that particular mode is for.
 */
export type GuestMode = 'chat' | 'agent' | 'students';

interface GuestHeroProps {
  mode: GuestMode;
  onLogin: () => void;
  onRegister: () => void;
}

export function GuestHero({ mode, onLogin, onRegister }: GuestHeroProps) {
  const { t } = useTranslation();

  return (
    <div className="guest-hero">
      <ConexyLogo size={44} />
      <h1 className="guest-hero__title">{t('guest.title')}</h1>
      <p className="guest-hero__subtitle">{t(`guest.subtitle_${mode}`)}</p>
      <div className="guest-hero__actions">
        <button className="guest-hero__btn" onClick={onLogin} type="button">
          {t('guest.login')}
        </button>
        <button className="guest-hero__btn guest-hero__btn--primary" onClick={onRegister} type="button">
          {t('guest.register')}
        </button>
      </div>
    </div>
  );
}
