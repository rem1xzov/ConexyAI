import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { signalrService, type ConnectionStatus } from '../services/signalrService';

// SIGNALR_RESILIENCE: добавлено 2026-09-22
/**
 * Visible connection state. Before this, a dropped socket left the agent spinner running forever
 * with no way for the user to tell whether the task was still alive or the link was simply gone.
 *
 * It renders nothing while the connection is healthy, so it never competes with the normal UI.
 */
export function ConnectionBanner() {
  const { t } = useTranslation();
  const [status, setStatus] = useState<ConnectionStatus>('disconnected');

  useEffect(() => signalrService.onStateChange(setStatus), []);

  if (status !== 'reconnecting' && status !== 'reconnect-failed') return null;

  const failed = status === 'reconnect-failed';

  return (
    <div
      className={`conn-banner ${failed ? 'conn-banner--failed' : ''}`}
      role="status"
      aria-live="polite"
    >
      {failed ? (
        <span className="conn-banner__mark" aria-hidden="true">!</span>
      ) : (
        <span className="conn-banner__spinner" aria-hidden="true" />
      )}
      <span className="conn-banner__text">
        {failed ? t('connection.lost') : t('connection.reconnecting')}
      </span>
      {failed && (
        <button
          type="button"
          className="conn-banner__action"
          onClick={() => window.location.reload()}
        >
          {t('connection.reload')}
        </button>
      )}
    </div>
  );
}
