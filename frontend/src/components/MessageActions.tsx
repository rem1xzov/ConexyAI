import { useState } from 'react';
import { CheckIcon, CopyIcon, RefreshIcon, ThumbsDownIcon, ThumbsUpIcon } from './Icons';

interface MessageActionsProps {
  content: string;
  onRegenerate?: () => void;
}

type Feedback = 'like' | 'dislike' | null;

/**
 * Compact action row shown under every assistant reply: copy raw markdown,
 * like/dislike (local UI state for now), and regenerate the last prompt.
 */
export function MessageActions({ content, onRegenerate }: MessageActionsProps) {
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

  return (
    <div className="message-actions">
      <button className="message-actions__btn" onClick={() => void copy()} title="Copy" aria-label="Copy">
        {copied ? <CheckIcon size={15} /> : <CopyIcon size={15} />}
      </button>
      <button
        className={`message-actions__btn ${feedback === 'like' ? 'message-actions__btn--active message-actions__btn--like' : ''}`}
        onClick={() => toggle('like')}
        title="Good response"
        aria-label="Good response"
      >
        <ThumbsUpIcon size={15} />
      </button>
      <button
        className={`message-actions__btn ${feedback === 'dislike' ? 'message-actions__btn--active message-actions__btn--dislike' : ''}`}
        onClick={() => toggle('dislike')}
        title="Bad response"
        aria-label="Bad response"
      >
        <ThumbsDownIcon size={15} />
      </button>
      {onRegenerate && (
        <button className="message-actions__btn" onClick={onRegenerate} title="Regenerate" aria-label="Regenerate">
          <RefreshIcon size={15} />
        </button>
      )}
    </div>
  );
}
