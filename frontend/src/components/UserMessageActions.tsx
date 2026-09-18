import { useState } from 'react';
import { CheckIcon, CopyIcon, EditIcon, RefreshIcon } from './Icons';

interface UserMessageActionsProps {
  content: string;
  onResend?: () => void;
  onEdit?: () => void;
}

/**
 * Compact action row shown under every user message: resend the same prompt,
 * edit it in place, or copy its text. Unlike the assistant actions row, there are
 * no like/dislike controls here.
 */
export function UserMessageActions({ content, onResend, onEdit }: UserMessageActionsProps) {
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

  return (
    <div className="message-actions">
      <button className="message-actions__btn" onClick={onResend} title="Resend" aria-label="Resend">
        <RefreshIcon size={15} />
      </button>
      <button className="message-actions__btn" onClick={onEdit} title="Edit" aria-label="Edit">
        <EditIcon size={15} />
      </button>
      <button className="message-actions__btn" onClick={() => void copy()} title="Copy" aria-label="Copy">
        {copied ? <CheckIcon size={15} /> : <CopyIcon size={15} />}
      </button>
    </div>
  );
}
