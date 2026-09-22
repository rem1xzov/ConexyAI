import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDownIcon, LightbulbIcon } from './Icons';

// AGENT_FEED_ZED: добавлено 2026-09-23
/**
 * One reasoning step of an agent run, in the compact Zed-style form: a light "Размышления"
 * header with a bulb icon that expands to reveal the reasoning text underneath.
 *
 * It replaces the old per-step row that printed the full backend label ("Обдумываю следующий шаг…")
 * plus a per-step stopwatch on every line — a stack of near-identical lines with a restarted
 * counter next to each of them, which is what made the feed look like it was looping. There is no
 * timer left here on purpose: a single honest counter for the whole turn lives at the bottom of the
 * feed, so nothing on screen resets to `1с` several times per answer.
 */
interface ThinkingStepProps {
  /** Reasoning text shown when the block is expanded. */
  text: string;
  /** True while this is still the step the agent is working in. */
  running: boolean;
}

export function ThinkingStep({ text, running }: ThinkingStepProps) {
  const { t } = useTranslation();
  // Open while the step is live, collapse once it is over — unless the user toggled it themselves,
  // in which case their choice wins for the rest of this step's life (tracked with a ref so the
  // auto-collapse does not fight a manual click).
  const [open, setOpen] = useState(running);
  const userToggled = useRef(false);

  useEffect(() => {
    if (userToggled.current) return;
    setOpen(running);
  }, [running]);

  function toggle() {
    userToggled.current = true;
    setOpen((v) => !v);
  }

  const body = text.trim() || t('timeline.thinkingEmpty');

  return (
    <div className={`think-step ${running ? 'think-step--running' : ''}`}>
      <button
        type="button"
        className="think-step__head"
        onClick={toggle}
        aria-expanded={open}
        title={t('timeline.thinking')}
      >
        <LightbulbIcon size={13} className="think-step__icon" />
        <span className="think-step__title">{t('timeline.thinking')}</span>
        {running && <span className="think-step__pulse" aria-hidden="true" />}
        <ChevronDownIcon
          size={12}
          className={`think-step__chevron ${open ? 'think-step__chevron--open' : ''}`}
        />
      </button>
      {open && <div className="think-step__body">{body}</div>}
    </div>
  );
}
