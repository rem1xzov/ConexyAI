import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { UserProfile } from '../types/api';
import { LogoutIcon, ShieldIcon } from './Icons';

interface AccountWidgetProps {
  user: UserProfile;
  onLogout: () => void;
  onUpgrade: () => void;
  onOpenAdmin: () => void;
  onOpenSupport: () => void;
  onOpenSettings: () => void;
}

// EMAIL_AUTH: добавлено 2026-09-19
/** Compact account widget (avatar initial + display name + tier) with a dropdown menu. */
export function AccountWidget({ user, onLogout, onUpgrade, onOpenAdmin, onOpenSupport, onOpenSettings }: AccountWidgetProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    function onClickOutside(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as HTMLElement)) {
        setOpen(false);
      }
    }
    document.addEventListener('mousedown', onClickOutside);
    return () => document.removeEventListener('mousedown', onClickOutside);
  }, []);

  const displayName = user.displayName || user.email || t('account.user');
  const initial = displayName.charAt(0).toUpperCase();

  return (
    <div className="account-widget" ref={ref}>
      <button
        className="account-widget__trigger"
        onClick={() => setOpen((o) => !o)}
        aria-label={t('account.settings')}
        type="button"
      >
        <span className="account-widget__avatar">{initial}</span>
        <span className="account-widget__meta">
          <span className="account-widget__name">{displayName}</span>
          <span className="account-widget__tier">{user.tier}</span>
        </span>
      </button>

      {open && (
        <div className="account-widget__menu">
          <button className="account-widget__item" onClick={() => { setOpen(false); onOpenSettings(); }} type="button">
            {t('account.settings')}
          </button>
          <button className="account-widget__item" onClick={() => { setOpen(false); onUpgrade(); }} type="button">
            {t('account.upgrade')}
          </button>
          <button className="account-widget__item" onClick={() => { setOpen(false); onOpenSupport(); }} type="button">
            {t('account.support')}
          </button>
          {user.isAdmin && (
            <button
              className="account-widget__item account-widget__item--admin"
              onClick={() => { setOpen(false); onOpenAdmin(); }}
              type="button"
            >
              <ShieldIcon size={15} /> {t('account.adminPanel')}
            </button>
          )}
          <button
            className="account-widget__item account-widget__item--danger"
            onClick={onLogout}
            type="button"
          >
            <LogoutIcon size={15} /> {t('account.logout')}
          </button>
        </div>
      )}
    </div>
  );
}
