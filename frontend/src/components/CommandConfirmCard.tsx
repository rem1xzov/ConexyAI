import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { CommandDecisionHandler, ToolActionEvent } from '../types/signalr';
import { CheckIcon, CloseIcon } from './Icons';

// COMMAND_FEEDBACK: переписано 2026-09-22
/**
 * Compact, non-blocking confirmation card for an agent command that actually needs a decision
 * (installs, deletes, writes — read-only diagnostics never get here).
 *
 * The card used to change nothing at all on click: the status came exclusively from server events,
 * so between "approved" and "command finished" the buttons stayed on screen and the user could not
 * tell whether the click registered. It now keeps a local decision state, so the click is
 * acknowledged instantly, and only then hands over to the backend's completed/failed event.
 */
interface CommandConfirmCardProps {
  /** Events sharing one pending-action id: the request and its eventual outcome. */
  events: ToolActionEvent[];
  onDecision?: CommandDecisionHandler;
}

type LocalDecision = 'none' | 'approving' | 'denying' | 'failed';

export function CommandConfirmCard({ events, onDecision }: CommandConfirmCardProps) {
  const { t } = useTranslation();
  const [allowAll, setAllowAll] = useState(false);
  const [showOutput, setShowOutput] = useState(false);
  const [decision, setDecision] = useState<LocalDecision>('none');

  const first = events[0];
  const completed = events.find((e) => e.status === 'completed' || e.status === 'failed');
  const rejected = events.find((e) => e.status === 'rejected');

  const isDangerous = events.some((e) => e.isDangerous);
  const actionId = first.pendingActionId;
  const output = completed?.output ?? '';

  // The authoritative outcome comes from the server; the local decision only fills the gap while
  // the command is in flight.
  const status: 'pending_confirmation' | 'approved' | 'rejected' | 'completed' | 'failed' | 'error' =
    rejected
      ? 'rejected'
      : completed
        ? completed.status === 'failed'
          ? 'failed'
          : 'completed'
        : decision === 'failed'
          ? 'error'
          : decision === 'approving'
            ? 'approved'
            : decision === 'denying'
              ? 'rejected'
              : 'pending_confirmation';

  const statusLabel =
    status === 'completed'
      ? t('toolAction.completed')
      : status === 'failed'
        ? t('toolAction.failed')
        : status === 'rejected'
          ? t('cmdConfirm.denied')
          : status === 'approved'
            ? t('cmdConfirm.approved')
            : status === 'error'
              ? t('cmdConfirm.sendFailed')
              : t('toolAction.pending');

  const statusMark =
    status === 'completed' ? (
      <span className="chat-ok">✓</span>
    ) : status === 'rejected' || status === 'failed' ? (
      <span className="chat-danger">{status === 'rejected' ? '⊘' : '✕'}</span>
    ) : status === 'error' ? (
      <span className="chat-danger">!</span>
    ) : status === 'approved' ? (
      <span className="cmd-confirm__spinner" aria-hidden="true" />
    ) : (
      <span className="chat-warn animate-pulse">●</span>
    );

  const settled = status === 'completed' || status === 'failed' || status === 'rejected';
  const inFlight = status === 'approved';
  const canDecide =
    !settled && !inFlight && decision !== 'denying' && Boolean(actionId) && Boolean(onDecision);

  async function decide(approved: boolean) {
    if (!actionId || !onDecision) return;
    // Acknowledge immediately — the user must see the click land, whatever the network does.
    setDecision(approved ? 'approving' : 'denying');
    const delivered = await onDecision(actionId, approved, allowAll);
    if (!delivered) {
      setDecision('failed');
      return;
    }
    // Keep the optimistic state until the server's terminal event replaces it.
  }

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

      {(canDecide || status === 'error') && (
        <div className="cmd-confirm__actions">
          <button
            className="cmd-confirm__btn cmd-confirm__btn--allow"
            onClick={() => void decide(true)}
            type="button"
          >
            <CheckIcon size={13} /> {t('cmdConfirm.allow')}
          </button>
          <button
            className="cmd-confirm__btn cmd-confirm__btn--deny"
            onClick={() => void decide(false)}
            type="button"
          >
            <CloseIcon size={13} /> {t('cmdConfirm.deny')}
          </button>
          {canDecide && (
            <label className="cmd-confirm__allow-all">
              <input
                type="checkbox"
                checked={allowAll}
                onChange={(e) => setAllowAll(e.target.checked)}
              />
              {t('cmdConfirm.allowAll')}
            </label>
          )}
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
