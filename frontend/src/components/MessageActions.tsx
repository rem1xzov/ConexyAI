import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { copyText } from '../utils/clipboard';
import { exportDocument, type OfficeFormat } from '../api/conexyApi';
import { triggerDownload } from '../utils/download';
import { CheckIcon, CopyIcon, DownloadIcon, PlayIcon, RefreshIcon, ThumbsDownIcon, ThumbsUpIcon } from './Icons';

// OFFICE_FORMATS: добавлено 2026-09-23 — every model's answer can be saved as an office file.
const EXPORT_FORMATS: { format: OfficeFormat; labelKey: string }[] = [
  { format: 'docx', labelKey: 'message.exportDocx' },
  { format: 'xlsx', labelKey: 'message.exportXlsx' },
  { format: 'pptx', labelKey: 'message.exportPptx' },
];

/** File name from the answer's first heading or line, safe for every OS. */
function exportFileName(content: string): string {
  const firstLine = content.split('\n').find((l) => l.trim().length > 0) ?? '';
  const cleaned = firstLine
    .replace(/^#+\s*/, '')
    .replace(/[*_`#[\]()<>:"/\\|?]/g, '')
    .trim()
    .slice(0, 60)
    .trim();
  return cleaned || 'conexy-answer';
}

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
  const [exportOpen, setExportOpen] = useState(false);
  const [exporting, setExporting] = useState<OfficeFormat | null>(null);
  const [exportFailed, setExportFailed] = useState(false);

  async function exportAs(format: OfficeFormat) {
    setExporting(format);
    setExportFailed(false);
    try {
      const name = exportFileName(content);
      triggerDownload(await exportDocument(format, content, name), `${name}.${format}`);
      setExportOpen(false);
    } catch {
      setExportFailed(true);
    } finally {
      setExporting(null);
    }
  }

  async function copy() {
    // copyText falls back to execCommand when the Clipboard API is missing (plain http).
    if (!(await copyText(content))) return;
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1500);
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
      <button
        type="button"
        className={`message-actions__btn ${exportOpen ? 'message-actions__btn--active' : ''}`}
        onClick={() => setExportOpen((o) => !o)}
        title={t('message.export')}
        aria-label={t('message.export')}
        aria-expanded={exportOpen}
      >
        <DownloadIcon size={15} />
      </button>
      {exportOpen &&
        EXPORT_FORMATS.map(({ format, labelKey }) => (
          <button
            key={format}
            type="button"
            className="message-actions__btn message-actions__btn--continue"
            onClick={() => void exportAs(format)}
            disabled={exporting !== null}
            title={t('message.export')}
          >
            <span>{exporting === format ? '…' : t(labelKey)}</span>
          </button>
        ))}
      {exportFailed && <span className="message-actions__error">{t('message.exportFailed')}</span>}
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
