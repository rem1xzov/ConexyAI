import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { CommandApproval, ToolActionEvent } from '../types/signalr';
import { CheckIcon, CloseIcon } from './Icons';

// COMMAND_CONFIRM: добавлено 2026-09-20
/**
 * Compact, non-blocking confirmation card for an agent `bash` command. It renders inline in
 * the action feed (chat message / agent status panel) instead of covering the whole app, so
 * the user can keep reading and scrolling while the agent waits for this specific command.
 *
 * Dangerous commands keep the amber accent; ordinary ones use the neutral surface.
 */
interface CommandConfirmCardProps {
  /** Events sharing one pending-action id: the pending request and its eventual outcome. */
  events: ToolActionEvent[];
  approval?: CommandApproval;
}

export function CommandConfirmCard({ events, approval }: CommandConfirmCardProps) {
  const { t } = useTranslation();
  const [allowAll, setAllowAll] = useState(false);
  const [showOutput, setShowOutput] = useState(false);

  const first = events[0];
  const completed = events.find((e) => e.status === 'completed' || e.status === 'failed');
  const rejected = events.find((e) => e.status === 'rejected');
  const pending = !completed && !rejected;

  const isDangerous = events.some((e) => e.isDangerous);
  const status: ToolActionEvent['status'] = rejected ? 'rejected' : completed ? completed.status : 'pending_confirmation';
  const output = completed?.output ?? '';
  const actionId = first.pendingActionId;

  const statusLabel =
    status === 'completed'
      ? t('toolAction.completed')
      : status === 'failed'
        ? t('toolAction.failed')
        : status === 'rejected'
          ? t('toolAction.rejected')
          : t('toolAction.pending');

  const statusMark =
    status === 'completed' ? (
      <span className="chat-ok">✓</span>
    ) : status === 'rejected' ? (
      <span className="chat-danger">⊘</span>
    ) : status === 'failed' ? (
      <span className="chat-danger">✕</span>
    ) : (
      <span className="chat-warn animate-pulse">●</span>
    );

  const canDecide = pending && Boolean(actionId) && Boolean(approval);

  return (
    <div
      className={`cmd-confirm cmd-confirm--${status} ${isDangerous ? 'cmd-confirm--danger' : ''}`}
      role={canDecide ? 'group' : undefined}
      aria-label={t('cmdConfirm.runCommand')}
    >
      <div className="cmd-confirm__head">
        <span className="cmd-confirm__title">{t('cmdConfirm.runCommand')}</span>
        {isDangerous && <span className="cmd-confirm__badge">{t('cmdConfirm.dangerous')}</span>}
        <span className="cmd-confirm__status">
          {statusMark}
          <span className="cmd-confirm__status-label">{statusLabel}</span>
        </span>
      </div>

      <pre className="cmd-confirm__command">{first.command}</pre>

      {first.workingDirectory && (
        <div className="cmd-confirm__dir" title={first.workingDirectory}>
          {first.workingDirectory}
        </div>
      )}

      {canDecide && (
        <div className="cmd-confirm__actions">
          <button
            className="cmd-confirm__btn cmd-confirm__btn--allow"
            onClick={() => approval!.onDecision(actionId!, true, allowAll)}
            type="button"
          >
            <CheckIcon size={13} /> {t('cmdConfirm.allow')}
          </button>
          <button
            className="cmd-confirm__btn cmd-confirm__btn--deny"
            onClick={() => approval!.onDecision(actionId!, false, false)}
            type="button"
          >
            <CloseIcon size={13} /> {t('cmdConfirm.deny')}
          </button>
          <label className="cmd-confirm__allow-all">
            <input
              type="checkbox"
              checked={allowAll}
              onChange={(e) => setAllowAll(e.target.checked)}
            />
            {t('cmdConfirm.allowAll')}
          </label>
        </div>
      )}

      {output && (
        <div className="cmd-confirm__output">
          <button
            className="danger-cmd-feed-toggle"
            onClick={() => setShowOutput((o) => !o)}
            type="button"
          >
            {showOutput ? t('toolAction.hideOutput') : t('toolAction.showOutput')}
          </button>
          {showOutput && <pre className="cmd-confirm__pre">{output}</pre>}
        </div>
      )}
    </div>
  );
}
