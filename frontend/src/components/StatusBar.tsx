import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { signalrService, type ConnectionStatus } from '../services/signalrService';

interface CursorInfo {
  line: number;
  column: number;
  language: string;
}

interface StatusBarProps {
  agentStatus: string;
  cursor: CursorInfo | null;
}

export function StatusBar({ agentStatus, cursor }: StatusBarProps) {
  const { t } = useTranslation();
  const [status, setStatus] = useState<ConnectionStatus>('disconnected');

  useEffect(() => signalrService.onStateChange(setStatus), []);

  const connectionLabel: Record<ConnectionStatus, string> = {
    connected: t('statusbar.connected'),
    connecting: t('statusbar.connecting'),
    reconnecting: t('statusbar.reconnecting'),
    'reconnect-failed': t('statusbar.reconnectFailed'),
    disconnecting: t('statusbar.disconnecting'),
    disconnected: t('statusbar.disconnected'),
  };

  const agentStatusLabel =
    agentStatus === 'Working…'
      ? t('statusbar.agentWorking')
      : agentStatus === 'Completed'
        ? t('statusbar.agentCompleted')
        : agentStatus === 'Failed'
          ? t('statusbar.agentFailed')
          : agentStatus === 'Stopped'
            ? t('statusbar.agentStopped')
            : t('statusbar.agentReady');

  return (
    <footer className="statusbar">
      <div className="statusbar__left">
        <span className={`statusbar__conn statusbar__conn--${status}`} />
        <span className="statusbar__agent" title={agentStatusLabel}>
          {agentStatusLabel}
        </span>
      </div>
      <div className="statusbar__right">
        {cursor && (
          <>
            <span className="statusbar__item">{t('statusbar.lineCol', { line: cursor.line, column: cursor.column })}</span>
            <span className="statusbar__item">UTF-8</span>
            <span className="statusbar__item">{cursor.language}</span>
          </>
        )}
        <span className={`statusbar__conn-label statusbar__conn-label--${status}`}>
          {connectionLabel[status]}
        </span>
      </div>
    </footer>
  );
}
