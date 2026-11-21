import { memo, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDownIcon, LightbulbIcon, SearchIcon } from './Icons';

/**
 * REASONING_BLOCK
 * Блок «Размышления» в стиле мобильного DeepSeek:
 *  - во время генерации — окно-телесуфлёр с маской и автоскроллом;
 *  - после завершения — компактная строка «Размышлял N сек ›»;
 *  - таймер живёт внутри заголовка, отдельного счётчика под блоком нет.
 */
export interface ThinkingSearchInfo {
  count: number;
  hosts: string[];
  onClick?: () => void;
}

interface ThinkingStepProps {
  /** Reasoning text shown when expanded. */
  text: string;
  /** True while the model is still thinking in this step. */
  running: boolean;
  /** When thinking started (for the in-header timer). */
  startedAt?: number;
  /** Final duration for a finished block, if the source already knows it. */
  durationMs?: number;
  /** Optional web-search step rendered inside the reasoning flow. */
  search?: ThinkingSearchInfo;
}

const STREAM_MAX_HEIGHT = 124;
const STREAM_MASK = 'linear-gradient(to bottom, transparent 0%, black 20%, black 80%, transparent 100%)';

function formatDuration(ms: number): string {
  const seconds = Math.max(1, Math.round(ms / 1000));
  return `${seconds} сек`;
}

export const ThinkingStep = memo(function ThinkingStep({
  text,
  running,
  startedAt,
  durationMs,
  search,
}: ThinkingStepProps) {
  const { t } = useTranslation();
  // null — автоматический режим: во время генерации открыто, после — свёрнуто.
  const [manual, setManual] = useState<boolean | null>(null);
  const [elapsed, setElapsed] = useState(() =>
    startedAt ? Math.max(0, Date.now() - startedAt) : 0,
  );
  const streamRef = useRef<HTMLDivElement>(null);
  const finalRef = useRef(durationMs ?? 0);

  // Сброс ручного выбора на смене фазы: завершение всегда сворачивает блок.
  useEffect(() => {
    setManual(null);
  }, [running]);

  // Живой таймер в заголовке.
  useEffect(() => {
    if (!running) {
      if (startedAt && !durationMs) finalRef.current = Math.max(finalRef.current, elapsed);
      return;
    }
    const from = startedAt ?? Date.now();
    const tick = () => setElapsed(Math.max(0, Date.now() - from));
    tick();
    const timer = window.setInterval(tick, 500);
    return () => window.clearInterval(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [running, startedAt]);

  // Автоскролл окна-телесуфлёра к последним пришедшим токенам.
  useEffect(() => {
    if (!running || manual === true) return;
    const el = streamRef.current;
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  }, [text, running, manual]);

  const bodyVisible = manual ?? running;
  const expanded = manual === true;
  const finalMs = durationMs ?? finalRef.current ?? elapsed;
  const shownMs = running ? elapsed : finalMs;
  const title = running
    ? t('timeline.reasoningActive')
    : t('timeline.reasonedFor', { duration: formatDuration(shownMs) });

  function toggle() {
    setManual(bodyVisible ? false : true);
  }

  return (
    <div className={`think-step${running ? ' think-step--running' : ''}`}>
      <button
        type="button"
        className="think-step__head"
        onClick={toggle}
        aria-expanded={bodyVisible}
        title={title}
      >
        <LightbulbIcon size={14} className="think-step__icon" />
        <span className="think-step__title">{title}</span>
        {running && <span className="think-step__pulse" aria-hidden="true" />}
        {running && <span className="think-step__timer">{formatDuration(shownMs)}</span>}
        <ChevronDownIcon
          size={14}
          className={`think-step__chevron ${bodyVisible ? 'think-step__chevron--open' : ''}`}
        />
      </button>

      <div
        className={`think-step__panel${bodyVisible ? '' : ' is-collapsed'}${expanded ? ' think-step__panel--expanded' : ''}`}
        aria-hidden={!bodyVisible}
      >
        {bodyVisible && text.trim() && (
          expanded ? (
            <div className="think-step__body think-step__body--full">{text}</div>
          ) : (
            <div
              ref={streamRef}
              className="think-step__stream"
              style={{
                maxHeight: STREAM_MAX_HEIGHT,
                WebkitMaskImage: STREAM_MASK,
                maskImage: STREAM_MASK,
              }}
            >
              {text}
            </div>
          )
        )}
        {bodyVisible && search && search.count > 0 && (
          <button
            type="button"
            className="think-step__search"
            onClick={search.onClick}
            disabled={!search.onClick}
          >
            <SearchIcon size={14} />
            <span>{t('timeline.webPages', { count: search.count })}</span>
            <span className="think-step__favicons" aria-hidden="true">
              {search.hosts.slice(0, 4).map((host) => (
                <img
                  key={host}
                  src={`https://www.google.com/s2/favicons?domain=${encodeURIComponent(host)}&sz=32`}
                  alt=""
                  loading="lazy"
                />
              ))}
            </span>
          </button>
        )}
      </div>
    </div>
  );
});
