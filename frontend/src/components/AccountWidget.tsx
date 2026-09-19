import { useEffect, useRef, useState } from 'react';
import type { UserProfile } from '../types/api';
import { LogoutIcon, ShieldIcon } from './Icons';

interface AccountWidgetProps {
  user: UserProfile;
  onLogout: () => void;
  onToast: (message: string) => void;
}

// EMAIL_AUTH: добавлено 2026-09-19
/**
 * Compact account widget (avatar initial + display name + tier) with a dropdown menu.
 * "Settings", "Upgrade plan" and "Admin panel" are stubs for now.
 */
export function AccountWidget({ user, onLogout, onToast }: AccountWidgetProps) {
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

  const displayName = user.displayName || user.email || 'Пользователь';
  const initial = displayName.charAt(0).toUpperCase();

  function stub(label: string) {
    setOpen(false);
    onToast(`${label} скоро появится`);
  }

  return (
    <div className="account-widget" ref={ref}>
      <button
        className="account-widget__trigger"
        onClick={() => setOpen((o) => !o)}
        aria-label="Аккаунт"
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
          <button className="account-widget__item" onClick={() => stub('Настройки')} type="button">
            Настройки
          </button>
          <button className="account-widget__item" onClick={() => stub('Улучшение плана')} type="button">
            Улучшить план
          </button>
          {user.isAdmin && (
            <button
              className="account-widget__item account-widget__item--admin"
              onClick={() => stub('Админ-панель')}
              type="button"
            >
              <ShieldIcon size={15} /> Админ-панель
            </button>
          )}
          <button
            className="account-widget__item account-widget__item--danger"
            onClick={onLogout}
            type="button"
          >
            <LogoutIcon size={15} /> Выйти
          </button>
        </div>
      )}
    </div>
  );
}
