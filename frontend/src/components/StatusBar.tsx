import { useEffect, useState } from 'react';
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

const CONNECTION_LABEL: Record<ConnectionStatus, string> = {
  connected: 'Connected',
  connecting: 'Connecting…',
  reconnecting: 'Reconnecting…',
  disconnecting: 'Disconnecting…',
  disconnected: 'Offline',
};

export function StatusBar({ agentStatus, cursor }: StatusBarProps) {
  const [status, setStatus] = useState<ConnectionStatus>('disconnected');

  useEffect(() => signalrService.onStateChange(setStatus), []);

  return (
    <footer className="statusbar">
      <div className="statusbar__left">
        <span className={`statusbar__conn statusbar__conn--${status}`} />
        <span className="statusbar__agent" title={agentStatus}>
          {agentStatus}
        </span>
      </div>
      <div className="statusbar__right">
        {cursor && (
          <>
            <span className="statusbar__item">Ln {cursor.line}, Col {cursor.column}</span>
            <span className="statusbar__item">UTF-8</span>
            <span className="statusbar__item">{cursor.language}</span>
          </>
        )}
        <span className={`statusbar__conn-label statusbar__conn-label--${status}`}>
          {CONNECTION_LABEL[status]}
        </span>
      </div>
    </footer>
  );
}
