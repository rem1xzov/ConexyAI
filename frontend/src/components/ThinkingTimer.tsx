import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';

// THINKING_TIMER: добавлено 2026-09-22
// Живой счётчик рядом с бегущим лого: по нему видно, что агент работает, а не завис.
// Время считается на клиенте от startedAt (timestamp начала прогона), поэтому счётчик не
// зависит от сети и не запрашивает бэкенд каждую секунду.

/**
 * Formats elapsed time the way the reference does: `12с` under a minute, `1м 5с` above it.
 * Exported for tests and for reuse in other running indicators.
 */
export function formatThinkingDuration(totalSeconds: number): string {
  const safe = Math.max(0, Math.floor(totalSeconds));
  if (safe < 60) return `${safe}с`;
  const minutes = Math.floor(safe / 60);
  const seconds = safe % 60;
  return `${minutes}м ${seconds}с`;
}

interface ThinkingTimerProps {
  /** Timestamp (ms) when the current run started. */
  startedAt: number;
}

/**
 * "Думает 3с" ticker. Mounted only while a run is in progress, so it naturally disappears when
 * the answer starts streaming to completion — it must never restart mid-run, which is why the
 * anchor is the message's own start timestamp rather than a local mount time.
 */
export function ThinkingTimer({ startedAt }: ThinkingTimerProps) {
  const { t } = useTranslation();
  const [elapsed, setElapsed] = useState(() => elapsedSeconds(startedAt));

  useEffect(() => {
    // Re-anchor immediately: a run that started before this component mounted should not
    // display 0с until the first tick.
    setElapsed(elapsedSeconds(startedAt));
    const id = window.setInterval(() => setElapsed(elapsedSeconds(startedAt)), 1000);
    return () => window.clearInterval(id);
  }, [startedAt]);

  return (
    <span className="thinking-timer" role="status" aria-live="polite">
      {t('message.thinkingFor', { duration: formatThinkingDuration(elapsed) })}
    </span>
  );
}

function elapsedSeconds(startedAt: number): number {
  return Math.max(0, Math.floor((Date.now() - startedAt) / 1000));
}
