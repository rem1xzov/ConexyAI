import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import QRCode from 'qrcode';
import {
  cancelTelegramLogin,
  getTelegramLoginStatus,
  sendTelegramPassword,
  startTelegramLogin,
  type TelegramLoginStatus,
} from '../api/deviceApi';
import { humanError } from '../utils/humanError';
import { isMobileApp, openExternalUrl } from '../utils/mobile';

// TELEGRAM_LOGIN: подключение личного Telegram из настроек (сайт и приложение). Нужны только api_id и
// api_hash; подтверждение входа — одно касание в Telegram (ссылка) или скан QR. Строку сессии вводить не нужно.
export function TelegramConnect() {
  const { t } = useTranslation();
  const [apiId, setApiId] = useState('');
  const [apiHash, setApiHash] = useState('');
  const [password, setPassword] = useState('');
  const [status, setStatus] = useState<TelegramLoginStatus>({ state: 'idle' });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [qr, setQr] = useState<string | null>(null);
  const polling = useRef(false);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  useEffect(() => {
    if (!status.url) {
      setQr(null);
      return;
    }
    let cancelled = false;
    void QRCode.toDataURL(status.url, { margin: 1, width: 220 }).then((data) => {
      if (!cancelled) setQr(data);
    });
    return () => {
      cancelled = true;
    };
  }, [status.url]);

  async function poll() {
    if (polling.current) return;
    polling.current = true;
    try {
      while (mounted.current) {
        const next = await getTelegramLoginStatus();
        if (!mounted.current) return;
        setStatus(next);
        if (next.state !== 'waiting' && next.state !== 'starting') return;
      }
    } catch (e) {
      if (mounted.current) setError(humanError(e, t));
    } finally {
      polling.current = false;
    }
  }

  async function start() {
    setError(null);
    setBusy(true);
    setStatus({ state: 'starting' });
    try {
      const next = await startTelegramLogin(apiId.trim(), apiHash.trim());
      setStatus(next);
      if (next.state === 'waiting' || next.state === 'starting') void poll();
    } catch (e) {
      setStatus({ state: 'idle' });
      setError(humanError(e, t));
    } finally {
      setBusy(false);
    }
  }

  async function submitPassword() {
    setError(null);
    setBusy(true);
    try {
      setStatus(await sendTelegramPassword(password));
      setPassword('');
      void poll();
    } catch (e) {
      setError(humanError(e, t));
    } finally {
      setBusy(false);
    }
  }

  async function cancel() {
    await cancelTelegramLogin().catch(() => undefined);
    setStatus({ state: 'idle' });
  }

  const state = status.state;

  return (
    <div className="settings-panel">
      <div className="settings-modal__section">
        <div className="settings-modal__label">{t('telegram.title')}</div>
        <p className="settings-github__hint">{t('telegram.intro')}</p>
      </div>

      {(state === 'idle' || state === 'failed') && (
        <div className="settings-modal__section telegram-connect__form">
          <ol className="telegram-connect__steps">
            <li>
              {t('telegram.step1')}{' '}
              <a href="https://my.telegram.org/apps" target="_blank" rel="noopener noreferrer"
                onClick={(e) => {
                  if (isMobileApp()) {
                    e.preventDefault();
                    openExternalUrl('https://my.telegram.org/apps');
                  }
                }}>
                my.telegram.org/apps
              </a>
            </li>
            <li>{t('telegram.step2')}</li>
          </ol>
          <input className="dialog-input settings-github__input" inputMode="numeric" placeholder="api_id" value={apiId}
            onChange={(e) => setApiId(e.target.value)} autoComplete="off" />
          <input className="dialog-input settings-github__input" placeholder="api_hash" value={apiHash}
            onChange={(e) => setApiHash(e.target.value)} autoComplete="off" spellCheck={false} />
          <button type="button" className="dialog-btn dialog-btn--primary" disabled={busy || !apiId.trim() || !apiHash.trim()}
            onClick={() => void start()}>
            {t('telegram.connect')}
          </button>
          {state === 'failed' && status.error && <p className="settings-github__error">{status.error}</p>}
        </div>
      )}

      {state === 'starting' && (
        <div className="settings-modal__section">
          <p className="settings-github__hint">{t('telegram.starting')}</p>
        </div>
      )}

      {state === 'waiting' && status.url && (
        <div className="settings-modal__section telegram-connect__confirm">
          {isMobileApp() || window.matchMedia('(max-width: 768px)').matches ? (
            <>
              <p>{t('telegram.confirmPhone')}</p>
              <button type="button" className="dialog-btn dialog-btn--primary"
                onClick={() => (isMobileApp() ? openExternalUrl(status.url!) : window.location.assign(status.url!))}>
                {t('telegram.openTelegram')}
              </button>
              <p className="settings-github__hint">{t('telegram.orScan')}</p>
            </>
          ) : (
            <p>{t('telegram.confirmScan')}</p>
          )}
          {qr && <img className="telegram-connect__qr" src={qr} alt="QR" width={220} height={220} />}
          <button type="button" className="dialog-btn" onClick={() => void cancel()}>{t('telegram.cancel')}</button>
        </div>
      )}

      {state === 'password' && (
        <div className="settings-modal__section telegram-connect__form">
          <p>{t('telegram.password')}</p>
          {status.badPassword && <p className="settings-github__error">{t('telegram.badPassword')}</p>}
          <input className="dialog-input settings-github__input" type="password" value={password} autoComplete="off"
            onChange={(e) => setPassword(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && password) void submitPassword();
            }} />
          <button type="button" className="dialog-btn dialog-btn--primary" disabled={busy || !password}
            onClick={() => void submitPassword()}>
            {t('telegram.signIn')}
          </button>
        </div>
      )}

      {state === 'done' && (
        <div className="settings-modal__section">
          <p className="telegram-connect__done">✓ {t('telegram.done', { user: status.user ?? '' })}</p>
        </div>
      )}

      {error && <p className="settings-github__error">{error}</p>}
    </div>
  );
}
