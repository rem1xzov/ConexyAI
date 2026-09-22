import { useEffect, useState } from 'react';

// AGENT_TIMELINE: добавлено 2026-09-22
// Таймер шага. Раньше один счётчик показывался под всей задачей и был привязан к createdAt
// сообщения: он тикал сквозь все шаги, показывал бессмысленные «12м 11с» и визуально «сбрасывался»
// каждый раз, когда лого переезжало вниз под новый блок. Теперь у каждого шага свой интервал,
// который привязан к startedAt ИМЕННО этого шага и очищается, как только шаг закрыт (endedAt).

/**
 * Formats elapsed time: `12с` under a minute, `1м 5с` above it. Exported for reuse and tests.
 */
export function formatDuration(totalSeconds: number): string {
  const safe = Math.max(0, Math.floor(totalSeconds));
  if (safe < 60) return `${safe}с`;
  const minutes = Math.floor(safe / 60);
  const seconds = safe % 60;
  return `${minutes}м ${seconds}с`;
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
      {formatDuration(elapsed)}
    </span>
  );
}

function secondsBetween(startedAt: number, endedAt?: number): number {
  const end = endedAt ?? Date.now();
  return Math.max(0, Math.floor((end - startedAt) / 1000));
}
