import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { copyText } from '../utils/clipboard';
import { exportDocument, type OfficeFormat } from '../api/conexyApi';
import { triggerDownload } from '../utils/download';
import {
  CheckIcon,
  CopyIcon,
  DownloadIcon,
  PlayIcon,
  RefreshIcon,
  ThreeDotsIcon,
  ThumbsDownIcon,
  ThumbsUpIcon,
} from './Icons';

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
 * Action row under every assistant reply. Only the three actions people use most are on the
 * surface (like, dislike, copy) plus an overflow menu, Gemini-style: export, regenerate and
 * continue live behind "…" so the chat stays quiet on a phone.
 */
export function MessageActions({ content, onRegenerate, onContinue }: MessageActionsProps) {
  const { t } = useTranslation();
  const [feedback, setFeedback] = useState<Feedback>(null);
  const [copied, setCopied] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [exporting, setExporting] = useState<OfficeFormat | null>(null);
  const [exportFailed, setExportFailed] = useState(false);
  const menuRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!menuOpen) return;
    function onPointerDown(event: PointerEvent) {
      const target = event.target as Node | null;
      if (target && menuRef.current && !menuRef.current.contains(target)) setMenuOpen(false);
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') setMenuOpen(false);
    }
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [menuOpen]);

  async function exportAs(format: OfficeFormat) {
    setExporting(format);
    setExportFailed(false);
    try {
      const name = exportFileName(content);
      triggerDownload(await exportDocument(format, content, name), `${name}.${format}`);
      setMenuOpen(false);
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

  const hasMenu = Boolean(onContinue || onRegenerate || EXPORT_FORMATS.length > 0);

  // BUGFIX_CONTINUE_CLICK: добавлено 2026-09-22 — у всех кнопок явный type="button". Без него
  // кнопка внутри формы по умолчанию считается submit и клик уходит в отправку формы, а не в
  // обработчик — именно так «Продолжить» могла выглядеть полностью нерабочей.
  return (
    <div className="message-actions">
      <button
        type="button"
        className={`message-actions__btn ${feedback === 'like' ? 'message-actions__btn--active message-actions__btn--like' : ''}`}
        onClick={() => toggle('like')}
        title={t('message.goodResponse')}
        aria-label={t('message.goodResponse')}
        aria-pressed={feedback === 'like'}
      >
        <ThumbsUpIcon size={16} />
      </button>
      <button
        type="button"
        className={`message-actions__btn ${feedback === 'dislike' ? 'message-actions__btn--active message-actions__btn--dislike' : ''}`}
        onClick={() => toggle('dislike')}
        title={t('message.badResponse')}
        aria-label={t('message.badResponse')}
        aria-pressed={feedback === 'dislike'}
      >
        <ThumbsDownIcon size={16} />
      </button>
      <button
        type="button"
        className="message-actions__btn"
        onClick={() => void copy()}
        title={t('message.copy')}
        aria-label={t('message.copy')}
      >
        {copied ? <CheckIcon size={16} /> : <CopyIcon size={16} />}
      </button>

      {hasMenu && (
        <div className="message-actions__more" ref={menuRef}>
          <button
            type="button"
            className={`message-actions__btn ${menuOpen ? 'message-actions__btn--active' : ''}`}
            onClick={() => setMenuOpen((open) => !open)}
            title={t('message.more')}
            aria-label={t('message.more')}
            aria-haspopup="menu"
            aria-expanded={menuOpen}
          >
            <ThreeDotsIcon size={16} />
          </button>
          {menuOpen && (
            <div className="message-actions__menu" role="menu">
              {onContinue && (
                <button
                  type="button"
                  role="menuitem"
                  className="message-actions__menu-item"
                  onClick={() => {
                    setMenuOpen(false);
                    onContinue();
                  }}
                >
                  <PlayIcon size={15} />
                  <span>{t('message.continue')}</span>
                </button>
              )}
              {EXPORT_FORMATS.map(({ format, labelKey }) => (
                <button
                  key={format}
                  type="button"
                  role="menuitem"
                  className="message-actions__menu-item"
                  onClick={() => void exportAs(format)}
                  disabled={exporting !== null}
                >
                  <DownloadIcon size={15} />
                  <span>{exporting === format ? '…' : t(labelKey)}</span>
                </button>
              ))}
              {onRegenerate && (
                <button
                  type="button"
                  role="menuitem"
                  className="message-actions__menu-item"
                  onClick={() => {
                    setMenuOpen(false);
                    onRegenerate();
                  }}
                >
                  <RefreshIcon size={15} />
                  <span>{t('message.regenerate')}</span>
                </button>
              )}
            </div>
          )}
        </div>
      )}
      {exportFailed && <span className="message-actions__error">{t('message.exportFailed')}</span>}
    </div>
  );
}
