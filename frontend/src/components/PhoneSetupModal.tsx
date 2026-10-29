import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  PHONE_PERMISSION_IDS,
  markPhoneOnboarded,
  refreshPhonePermissions,
  requestAllPhonePermissions,
  requestPhonePermission,
  usePhoneAgentState,
} from '../services/phoneAgent';
import { CheckIcon, CloseIcon } from './Icons';

// PHONE_AGENT: экран «ConexyAI Phone» — что агент сможет делать на телефоне и какие разрешения для
// этого нужны. Показывается при первом запуске нового APK и из Настроек → «Телефон».

/** Список разрешений со статусами и кнопками «Разрешить»; статусы живут по событию permissionsChanged. */
export function PhonePermissionsList() {
  const { t } = useTranslation();
  const { permissions } = usePhoneAgentState();
  const [busy, setBusy] = useState<string | null>(null);

  useEffect(() => {
    void refreshPhonePermissions().catch(() => undefined);
  }, []);

  async function allow(id: string) {
    setBusy(id);
    try {
      await requestPhonePermission(id);
    } catch {
      // статус всё равно перечитается ниже
    } finally {
      setBusy(null);
    }
  }

  return (
    <ul className="phone-perms" aria-label={t('phone.permissionsTitle')}>
      {PHONE_PERMISSION_IDS.map((id) => {
        const raw = permissions[id];
        const state = raw === 'granted' || raw === 'denied' || raw === 'prompt' || raw === 'unavailable' ? raw : 'prompt';
        const canAllow = state === 'prompt' || state === 'denied';
        return (
          <li key={id} className="phone-perm">
            <div className="phone-perm__text">
              <div className="phone-perm__name">{t(`phone.perm.${id}.name`)}</div>
              <div className="phone-perm__why">{t(`phone.perm.${id}.why`)}</div>
            </div>
            <span className={`phone-perm__state phone-perm__state--${state}`}>
              {state === 'granted' && <CheckIcon size={12} />} {t(`phone.status.${state}`)}
            </span>
            {canAllow && (
              <button
                type="button"
                className="dialog-btn phone-perm__btn"
                onClick={() => void allow(id)}
                disabled={busy !== null}
              >
                {busy === id ? t('common.loading') : t('phone.allow')}
              </button>
            )}
          </li>
        );
      })}
    </ul>
  );
}

export function PhoneSetupModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const [allBusy, setAllBusy] = useState(false);

  function close() {
    markPhoneOnboarded();
    onClose();
  }

  async function allowAll() {
    setAllBusy(true);
    try {
      await requestAllPhonePermissions();
    } catch {
      // пользователь мог отказать в диалоге — статусы покажут, что осталось
    } finally {
      setAllBusy(false);
    }
  }

  return (
    <div className="dialog-overlay" onMouseDown={close}>
      <div
        className="dialog-card phone-setup"
        role="dialog"
        aria-modal="true"
        aria-label={t('phone.setupTitle')}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="settings-modal__head">
          <div className="dialog-title">{t('phone.setupTitle')}</div>
          <button className="icon-btn" onClick={close} aria-label={t('common.close')} type="button">
            <CloseIcon size={18} />
          </button>
        </div>
        <p className="settings-panel__intro">{t('phone.setupIntro')}</p>
        <p className="settings-github__hint">{t('phone.setupPrivacy')}</p>

        <div className="phone-setup__list">
          <PhonePermissionsList />
        </div>

        <div className="dialog-actions phone-setup__actions">
          <button className="dialog-btn" onClick={close} type="button">
            {t('phone.later')}
          </button>
          <button className="dialog-btn dialog-btn--primary" onClick={() => void allowAll()} disabled={allBusy} type="button">
            {allBusy ? t('common.loading') : t('phone.allowAll')}
          </button>
        </div>
      </div>
    </div>
  );
}
