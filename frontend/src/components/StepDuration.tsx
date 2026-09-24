import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { TFunction } from 'i18next';
import i18n from '../i18n';

// AGENT_TIMELINE: добавлено 2026-09-22
// Таймер шага. Раньше один счётчик показывался под всей задачей и был привязан к createdAt
// сообщения: он тикал сквозь все шаги, показывал бессмысленные «12м 11с» и визуально «сбрасывался»
// каждый раз, когда лого переезжало вниз под новый блок. Теперь у каждого шага свой интервал,
// который привязан к startedAt ИМЕННО этого шага и очищается, как только шаг закрыт (endedAt).

/**
 * Formats elapsed time: `12с` / `12s` under a minute, `1м 5с` / `1m 5s` above it. Exported for
 * reuse and tests.
 *
 * I18N_L2: добавлено 2026-09-24 — единицы «с»/«м» были зашиты в код и оставались русскими в
 * английском интерфейсе; теперь это ключи render.durationSeconds / render.durationMinutes.
 */
export function formatDuration(totalSeconds: number, t: TFunction = i18n.t.bind(i18n)): string {
  const safe = Math.max(0, Math.floor(totalSeconds));
  if (safe < 60) return t('render.durationSeconds', { s: safe });
  const minutes = Math.floor(safe / 60);
  const seconds = safe % 60;
  return t('render.durationMinutes', { m: minutes, s: seconds });
}

interface DurationBadgeProps {
  /** Timestamp (ms) when this step started. */
  startedAt: number;
  /** Set once the step is over — the badge then shows a frozen duration and stops ticking. */
  endedAt?: number;
  className?: string;
}

/**
 * A per-step stopwatch. Only the still-running step owns an interval, so at most one timer is
 * alive at a time and finished steps can never drift or fight over the same counter.
 */
export function DurationBadge({ startedAt, endedAt, className }: DurationBadgeProps) {
  const { t } = useTranslation();
  const running = endedAt === undefined;
  const [elapsed, setElapsed] = useState(() => secondsBetween(startedAt, endedAt));

  useEffect(() => {
    setElapsed(secondsBetween(startedAt, endedAt));
    if (!running) return;

    const id = window.setInterval(() => setElapsed(secondsBetween(startedAt, endedAt)), 1000);
    // Explicit cleanup: without it a stale interval kept writing into an unmounted step and the
    // numbers on screen stopped matching the step they belonged to.
    return () => window.clearInterval(id);
  }, [startedAt, endedAt, running]);

  return (
    <span className={`step-duration ${className ?? ''}`.trim()} aria-hidden="true">
      {formatDuration(elapsed, t)}
    </span>
  );
}

function secondsBetween(startedAt: number, endedAt?: number): number {
  const end = endedAt ?? Date.now();
  return Math.max(0, Math.floor((end - startedAt) / 1000));
}
