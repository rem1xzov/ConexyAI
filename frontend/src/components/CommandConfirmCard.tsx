import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { CommandDecisionHandler, ToolActionEvent } from '../types/signalr';
import { CheckIcon, ChevronDownIcon, CloseIcon, CopyIcon } from './Icons';

// COMMAND_FEEDBACK: переписано 2026-09-22
// AGENT_FEED_ZED: перерисовано 2026-09-23 — теперь это тот самый терминальный блок из референса:
// заголовок «Run Command», команда моноширинным шрифтом, кнопка копирования в правом верхнем углу,
// приглушённая подпись статуса в шапке и сворачиваемый вывод под командой.
/**
 * Full-size block for an agent command that actually needs a decision (installs, deletes, writes —
 * read-only diagnostics never get here and are rendered as a plain `ActionStepLine` instead).
 *
 * This is the only element of the agent feed that keeps a border and a background, precisely
 * because it is the only one with something worth hiding: the command and its output.
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
  const [copied, setCopied] = useState(false);

  const first = events[0];
  const completed = events.find((e) => e.status === 'completed' || e.status === 'failed');
  const rejected = events.find((e) => e.status === 'rejected');

  const isDangerous = events.some((e) => e.isDangerous);
  const actionId = first.pendingActionId;
  const output = completed?.output ?? '';
  const command = first.command ?? '';

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

  // Reset the "copied" hint on its own; without the cleanup a second copy click could be swallowed
  // by a timer that was still pending from the first one.
  useEffect(() => {
    if (!copied) return;
    const id = window.setTimeout(() => setCopied(false), 1500);
    return () => window.clearTimeout(id);
  }, [copied]);

  async function copyCommand() {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
    } catch {
      // Clipboard unavailable (insecure context / denied permission): leave the button silent
      // rather than claiming the command was copied.
    }
  }

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
      className={`cmd-card cmd-confirm cmd-confirm--${status} ${isDangerous ? 'cmd-confirm--danger' : ''}`}
      role={canDecide ? 'group' : undefined}
      aria-label={t('cmdConfirm.runCommand')}
    >
      <div className="cmd-card__head">
        <span className="cmd-card__title">{t('cmdConfirm.runCommand')}</span>
        {isDangerous && <span className="cmd-card__badge">{t('cmdConfirm.dangerous')}</span>}
        <span className="cmd-card__status">
          {statusMark}
          <span className="cmd-card__status-label">{statusLabel}</span>
        </span>
      </div>

      <div className="cmd-card__codewrap">
        <pre className="cmd-card__command">{command}</pre>
        <button
          type="button"
          className={`cmd-card__copy ${copied ? 'cmd-card__copy--done' : ''}`}
          onClick={() => void copyCommand()}
          title={copied ? t('timeline.copied') : t('timeline.copyCommand')}
          aria-label={copied ? t('timeline.copied') : t('timeline.copyCommand')}
        >
          {copied ? <CheckIcon size={13} /> : <CopyIcon size={13} />}
        </button>
      </div>

      {first.workingDirectory && (
        <div className="cmd-card__dir" title={first.workingDirectory}>
          {first.workingDirectory}
        </div>
      )}

      {(canDecide || status === 'error') && (
        <div className="cmd-card__actions">
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
        <div className="cmd-card__output">
          <button
            type="button"
            className="cmd-card__output-toggle"
            onClick={() => setShowOutput((o) => !o)}
            aria-expanded={showOutput}
          >
            <ChevronDownIcon
              size={12}
              className={`cmd-card__output-chevron ${showOutput ? 'cmd-card__output-chevron--open' : ''}`}
            />
            {showOutput ? t('toolAction.hideOutput') : t('toolAction.showOutput')}
          </button>
          {showOutput && <pre className="cmd-card__pre">{output}</pre>}
        </div>
      )}
    </div>
  );
}
