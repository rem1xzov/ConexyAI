import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  deleteGoal,
  getActiveGoal,
  setGoalStatus,
  startGoal,
  type AgentGoal,
} from '../api/goalsApi';
import { CloseIcon } from './Icons';

interface GoalDialogProps {
  chatId: string | null;
  onClose: () => void;
}

function errorText(e: unknown, fallback: string): string {
  const message = (e as { response?: { data?: { message?: string } } }).response?.data?.message;
  return typeof message === 'string' && message.length > 0 ? message : fallback;
}

// GOAL: добавлено 2026-11-20 — панель долгоживущей цели. Сервер сам запускает раунды, здесь цель
// ставят на паузу/возобновляют/завершают и видят, на каком раунде она находится.
export function GoalDialog({ chatId, onClose }: GoalDialogProps) {
  const { t } = useTranslation();
  const [goal, setGoal] = useState<AgentGoal | null | undefined>(undefined);
  const [objective, setObjective] = useState('');
  const [maxRounds, setMaxRounds] = useState(10);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function reload() {
    if (!chatId) {
      setGoal(null);
      return;
    }
    try {
      const active = await getActiveGoal(chatId);
      setGoal(active);
      if (active) {
        setObjective(active.objective);
        setMaxRounds(active.maxRounds);
      }
    } catch (e) {
      setError(errorText(e, t('goal.loadError')));
      setGoal(null);
    }
  }

  useEffect(() => {
    void reload();
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [chatId]);

  async function submit() {
    if (!chatId) return;
    const text = objective.trim();
    if (!text) {
      setError(t('goal.objectiveRequired'));
      return;
    }
    setError(null);
    setNotice(null);
    setBusy(true);
    try {
      const saved = await startGoal(chatId, text, Math.min(50, Math.max(1, maxRounds)));
      setGoal(saved);
      setNotice(t('goal.started', { rounds: saved.maxRounds }));
    } catch (e) {
      setError(errorText(e, t('goal.actionError')));
    } finally {
      setBusy(false);
    }
  }

  async function changeStatus(status: 'active' | 'paused' | 'complete', reason?: string) {
    if (!chatId) return;
    setError(null);
    setNotice(null);
    setBusy(true);
    try {
      const updated = await setGoalStatus(chatId, status, reason);
      setGoal(updated);
      setNotice(
        status === 'complete'
          ? t('goal.completedNotice')
          : status === 'paused'
            ? t('goal.pausedNotice')
            : t('goal.resumedNotice'),
      );
    } catch (e) {
      setError(errorText(e, t('goal.actionError')));
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    if (!chatId || !window.confirm(t('goal.deleteConfirm'))) return;
    setBusy(true);
    try {
      await deleteGoal(chatId);
      setGoal(null);
      setObjective('');
      setNotice(t('goal.deletedNotice'));
    } catch (e) {
      setError(errorText(e, t('goal.actionError')));
    } finally {
      setBusy(false);
    }
  }

  const statusLabel = goal ? t(`goal.status.${goal.status}`) : '';
  const canStart = !goal || goal.status === 'blocked';

  return (
    <div className="dialog-overlay" onMouseDown={onClose}>
      <div
        className="dialog-card goal-dialog"
        role="dialog"
        aria-modal="true"
        aria-label={t('goal.title')}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <header className="goal-dialog__head">
          <span className="goal-dialog__title">{t('goal.title')}</span>
          <button type="button" className="goal-dialog__close" onClick={onClose} aria-label={t('common.close')}>
            <CloseIcon size={18} />
          </button>
        </header>

        <p className="goal-dialog__hint">{t('goal.hint')}</p>

        {!chatId && <div className="goal-dialog__error">{t('goal.noChat')}</div>}
        {error && <div className="goal-dialog__error">{error}</div>}
        {notice && <div className="goal-dialog__notice">{notice}</div>}

        {goal && (
          <section className="goal-dialog__current">
            <div className="goal-dialog__status">
              <span className={`goal-dialog__badge goal-dialog__badge--${goal.status}`}>{statusLabel}</span>
              <span className="goal-dialog__rounds">
                {t('goal.rounds', { started: goal.roundsStarted, max: goal.maxRounds })}
              </span>
            </div>
            <div className="goal-dialog__objective">{goal.objective}</div>
            {goal.blockedReason && <div className="goal-dialog__blocked">{goal.blockedReason}</div>}
            {goal.lastNote && (
              <div className="goal-dialog__note">
                <span className="goal-dialog__note-label">{t('goal.lastNote')}</span>
                {goal.lastNote}
              </div>
            )}
            <div className="goal-dialog__actions">
              {goal.status === 'active' && (
                <button type="button" className="goal-dialog__btn" disabled={busy} onClick={() => void changeStatus('paused')}>
                  {t('goal.pause')}
                </button>
              )}
              {(goal.status === 'paused' || goal.status === 'blocked') && (
                <button type="button" className="goal-dialog__btn" disabled={busy} onClick={() => void changeStatus('active')}>
                  {t('goal.resume')}
                </button>
              )}
              {goal.status !== 'complete' && (
                <button type="button" className="goal-dialog__btn" disabled={busy} onClick={() => void changeStatus('complete')}>
                  {t('goal.complete')}
                </button>
              )}
              <button type="button" className="goal-dialog__btn goal-dialog__btn--danger" disabled={busy} onClick={() => void remove()}>
                {t('goal.delete')}
              </button>
            </div>
          </section>
        )}

        {canStart && (
          <section className="goal-dialog__form">
            <label className="goal-dialog__label" htmlFor="goal-objective">
              {t('goal.objectiveLabel')}
            </label>
            <textarea
              id="goal-objective"
              className="goal-dialog__textarea"
              rows={4}
              maxLength={4000}
              value={objective}
              placeholder={t('goal.objectivePlaceholder')}
              onChange={(e) => setObjective(e.target.value)}
            />
            <label className="goal-dialog__label" htmlFor="goal-rounds">
              {t('goal.roundsLabel')}
            </label>
            <input
              id="goal-rounds"
              className="goal-dialog__rounds-input"
              type="number"
              min={1}
              max={50}
              value={maxRounds}
              onChange={(e) => setMaxRounds(Number(e.target.value) || 1)}
            />
            <button type="button" className="goal-dialog__btn goal-dialog__btn--primary" disabled={busy || !chatId} onClick={() => void submit()}>
              {goal ? t('goal.restart') : t('goal.start')}
            </button>
          </section>
        )}
      </div>
    </div>
  );
}
