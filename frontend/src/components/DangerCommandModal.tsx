import { useTranslation } from 'react-i18next';
import type { PendingActionPayload } from '../types/signalr';

// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
interface DangerCommandModalProps {
  action: PendingActionPayload;
  onApprove: () => void;
  onReject: () => void;
}

/**
 * Blocking confirmation modal for a dangerous `bash` command. Rendered above the whole
 * chat (overlay with the highest z-index) until the user approves or rejects it.
 */
export function DangerCommandModal({ action, onApprove, onReject }: DangerCommandModalProps) {
  const { t } = useTranslation();
  return (
    <div className="danger-cmd-overlay" role="dialog" aria-modal="true" aria-label={t('dangerCmd.ariaLabel')}>
      <div className="danger-cmd-card">
        <div className="danger-cmd-header">
          <span className="danger-cmd-icon">⚠</span>
          <span className="danger-cmd-title">{t('dangerCmd.title')}</span>
        </div>

        <div className="danger-cmd-body">
          <div className="danger-cmd-label">{t('dangerCmd.command')}</div>
          <pre className="danger-cmd-command">{action.command}</pre>

          <div className="danger-cmd-label">{t('dangerCmd.workingDirectory')}</div>
          <pre className="danger-cmd-dir">{action.workingDirectory}</pre>
        </div>

        <div className="danger-cmd-actions">
          <button className="dialog-btn dialog-btn--danger" onClick={onReject} type="button">
            {t('dangerCmd.reject')}
          </button>
          <button className="dialog-btn dialog-btn--primary" onClick={onApprove} type="button" autoFocus>
            {t('dangerCmd.approve')}
          </button>
        </div>
      </div>
    </div>
  );
}
