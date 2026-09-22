import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckIcon, CopyIcon, PlayIcon, RefreshIcon, ThumbsDownIcon, ThumbsUpIcon } from './Icons';

interface MessageActionsProps {
  content: string;
  onRegenerate?: () => void;
  // CONTINUE_GENERATION: добавлено 2026-09-21
  /** Shown only for an answer that was stopped mid-generation. */
  onContinue?: () => void;
}

type Feedback = 'like' | 'dislike' | null;

/**
 * Compact action row shown under every assistant reply: copy raw markdown,
 * like/dislike (local UI state for now), and regenerate the last prompt.
 */
export function MessageActions({ content, onRegenerate, onContinue }: MessageActionsProps) {
  const { t } = useTranslation();
  const [feedback, setFeedback] = useState<Feedback>(null);
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(content);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard may be unavailable (non-secure context); ignore.
    }
  }

  function toggle(next: Feedback) {
    setFeedback((prev) => (prev === next ? null : next));
  }

  // BUGFIX_CONTINUE_CLICK: добавлено 2026-09-22 — у всех кнопок явный type="button". Без него
  // кнопка внутри формы по умолчанию считается submit и клик уходит в отправку формы, а не в
  // обработчик — именно так «Продолжить» могла выглядеть полностью нерабочей.
  return (
    <div className="message-actions">
      <button
        type="button"
        className="message-actions__btn"
        onClick={() => void copy()}
        title={t('message.copy')}
        aria-label={t('message.copy')}
      >
        {copied ? <CheckIcon size={15} /> : <CopyIcon size={15} />}
      </button>
      <button
        type="button"
        className={`message-actions__btn ${feedback === 'like' ? 'message-actions__btn--active message-actions__btn--like' : ''}`}
        onClick={() => toggle('like')}
        title={t('message.goodResponse')}
        aria-label={t('message.goodResponse')}
      >
        <ThumbsUpIcon size={15} />
      </button>
      <button
        type="button"
        className={`message-actions__btn ${feedback === 'dislike' ? 'message-actions__btn--active message-actions__btn--dislike' : ''}`}
        onClick={() => toggle('dislike')}
        title={t('message.badResponse')}
        aria-label={t('message.badResponse')}
      >
        <ThumbsDownIcon size={15} />
      </button>
      {onContinue && (
        <button
          type="button"
          className="message-actions__btn message-actions__btn--continue"
          onClick={onContinue}
          title={t('message.continue')}
          aria-label={t('message.continue')}
        >
          <PlayIcon size={15} />
          <span>{t('message.continue')}</span>
        </button>
      )}
      {onRegenerate && (
        <button
          type="button"
          className="message-actions__btn"
          onClick={onRegenerate}
          title={t('message.regenerate')}
          aria-label={t('message.regenerate')}
        >
          <RefreshIcon size={15} />
        </button>
      )}
    </div>
  );
}
