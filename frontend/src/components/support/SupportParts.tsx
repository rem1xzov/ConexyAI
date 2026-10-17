import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { getSupportAttachmentBlob } from '../../api/conexyApi';
import type { SupportAttachment, SupportMessage } from '../../types/api';
import { CloseIcon, DownloadIcon, FileIcon, SendIcon, UploadIcon } from '../Icons';

// SUPPORT_ATTACHMENTS / SUPPORT_MULTI: добавлено 2026-10-18 — общие части мессенджера поддержки
// для пользователя и оператора: пузыри с вложениями, просмотр картинок, поле ввода с файлами.

export const MAX_FILES = 5;
export const MAX_FILE_BYTES = 10 * 1024 * 1024;
const BLOCKED = /\.(exe|msi|bat|cmd|com|scr|ps1|vbs|js|jse|wsf|hta|lnk|jar|apk|dll)$/i;

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function formatTime(iso: string, lang: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleTimeString(lang, { hour: '2-digit', minute: '2-digit' });
}

/** «14:05» сегодня, «вчера», «12 окт» — как в списке чатов мессенджера. */
export function formatListTime(iso: string, lang: string, yesterday: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const now = new Date();
  const day = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diff = Math.round((day(now) - day(d)) / 86_400_000);
  if (diff === 0) return formatTime(iso, lang);
  if (diff === 1) return yesterday;
  return d.toLocaleDateString(lang, { day: 'numeric', month: 'short' });
}

/** Добавляет сообщение, если его ещё нет (ответ HTTP + эхо из хаба не дублируются). */
export function withMessage<T extends { messages: SupportMessage[] }>(ticket: T, msg: SupportMessage): T {
  if (ticket.messages.some((m) => m.id === msg.id)) return ticket;
  return { ...ticket, messages: [...ticket.messages, msg] };
}

// Картинки грузим с токеном и держим object URL в кэше на время жизни страницы.
const blobUrls = new Map<string, Promise<string>>();
function attachmentUrl(id: string): Promise<string> {
  let url = blobUrls.get(id);
  if (!url) {
    url = getSupportAttachmentBlob(id).then((b) => URL.createObjectURL(b));
    url.catch(() => blobUrls.delete(id));
    blobUrls.set(id, url);
  }
  return url;
}

function ImageAttachment({ attachment, onOpen }: { attachment: SupportAttachment; onOpen: (url: string, name: string) => void }) {
  const [url, setUrl] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let alive = true;
    attachmentUrl(attachment.id).then((u) => alive && setUrl(u), () => alive && setFailed(true));
    return () => {
      alive = false;
    };
  }, [attachment.id]);

  if (failed) return <FileAttachment attachment={attachment} />;
  return (
    <button
      type="button"
      className="support-image"
      onClick={() => url && onOpen(url, attachment.fileName)}
      aria-label={attachment.fileName}
    >
      {url ? <img src={url} alt={attachment.fileName} loading="lazy" /> : <span className="support-image__loading" />}
    </button>
  );
}

function FileAttachment({ attachment }: { attachment: SupportAttachment }) {
  const [busy, setBusy] = useState(false);
  async function download() {
    setBusy(true);
    try {
      const url = await attachmentUrl(attachment.id);
      const a = document.createElement('a');
      a.href = url;
      a.download = attachment.fileName;
      a.click();
    } finally {
      setBusy(false);
    }
  }
  return (
    <button type="button" className="support-file" onClick={() => void download()} disabled={busy}>
      <span className="support-file__icon">
        <FileIcon size={18} />
      </span>
      <span className="support-file__meta">
        <span className="support-file__name">{attachment.fileName}</span>
        <span className="support-file__size">{formatSize(attachment.size)}</span>
      </span>
      <DownloadIcon size={16} />
    </button>
  );
}

export function messageClass(m: SupportMessage, operatorView: boolean): string {
  const own = operatorView ? m.authorType === 'Admin' : m.authorType === 'User';
  if (m.authorType === 'System') return 'support-msg support-msg--system';
  const role = m.authorType === 'Bot' ? ' support-msg--bot' : m.authorType === 'Admin' ? ' support-msg--admin' : '';
  return `support-msg ${own ? 'support-msg--own' : 'support-msg--other'}${role}`;
}

/** Лента сообщений: подписи авторов, картинки сеткой, файлы карточками, время. */
export function MessageList({
  messages,
  operatorView,
  lang,
  empty,
  scrollRef,
  typing,
}: {
  messages: SupportMessage[];
  operatorView: boolean;
  lang: string;
  empty?: React.ReactNode;
  scrollRef?: React.RefObject<HTMLDivElement>;
  typing?: boolean;
}) {
  const { t } = useTranslation();
  const [viewer, setViewer] = useState<{ url: string; name: string } | null>(null);

  useEffect(() => {
    if (!viewer) return;
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setViewer(null);
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [viewer]);

  return (
    <div className="support-thread__messages" ref={scrollRef}>
      {messages.length === 0 && empty}
      {messages.map((m) => {
        const images = (m.attachments ?? []).filter((a) => a.isImage);
        const files = (m.attachments ?? []).filter((a) => !a.isImage);
        return (
          <div key={m.id} className={messageClass(m, operatorView)}>
            {m.authorType === 'Bot' && <div className="support-msg__author">{t('support.botName')}</div>}
            {m.authorType === 'Admin' && !operatorView && <div className="support-msg__author">{t('support.operatorName')}</div>}
            {m.authorType === 'User' && operatorView && <div className="support-msg__author">{t('support.user')}</div>}
            {images.length > 0 && (
              <div className={`support-images support-images--${Math.min(images.length, 3)}`}>
                {images.map((a) => (
                  <ImageAttachment key={a.id} attachment={a} onOpen={(url, name) => setViewer({ url, name })} />
                ))}
              </div>
            )}
            {files.map((a) => (
              <FileAttachment key={a.id} attachment={a} />
            ))}
            {m.content && <div className="support-msg__bubble">{m.content}</div>}
            {m.authorType !== 'System' && <div className="support-msg__time">{formatTime(m.createdAt, lang)}</div>}
          </div>
        );
      })}
      {typing && (
        <div className="support-msg support-msg--other support-msg--bot">
          <div className="support-msg__author">{t('support.botName')}</div>
          <div className="support-msg__bubble support-typing">
            <span />
            <span />
            <span />
          </div>
        </div>
      )}
      {viewer && (
        <div className="support-viewer" onClick={() => setViewer(null)} role="dialog" aria-modal="true">
          <img src={viewer.url} alt={viewer.name} onClick={(e) => e.stopPropagation()} />
          <div className="support-viewer__bar" onClick={(e) => e.stopPropagation()}>
            <span>{viewer.name}</span>
            <a className="icon-btn" href={viewer.url} download={viewer.name} aria-label={t('support.download')}>
              <DownloadIcon size={18} />
            </a>
            <button className="icon-btn" type="button" onClick={() => setViewer(null)} aria-label={t('common.close')}>
              <CloseIcon size={18} />
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * Поле ввода как в мессенджере: многострочный текст (Enter — отправить, Shift+Enter — перенос),
 * скрепка, вставка картинки из буфера (Ctrl+V), перетаскивание файлов, превью перед отправкой.
 */
export function Composer({
  disabled,
  placeholder,
  onSend,
  onToast,
}: {
  disabled?: boolean;
  placeholder: string;
  onSend: (text: string, files: File[], onProgress: (p: number) => void) => Promise<boolean>;
  onToast: (message: string) => void;
}) {
  const { t } = useTranslation();
  const [text, setText] = useState('');
  const [files, setFiles] = useState<File[]>([]);
  const [sending, setSending] = useState(false);
  const [progress, setProgress] = useState<number | null>(null);
  const [dragging, setDragging] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const areaRef = useRef<HTMLTextAreaElement>(null);
  const previews = useRef(new Map<File, string>());

  useEffect(() => {
    const map = previews.current;
    return () => map.forEach((u) => URL.revokeObjectURL(u));
  }, []);

  // Высота поля растёт вместе с текстом, до ~6 строк.
  useEffect(() => {
    const el = areaRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 140)}px`;
  }, [text]);

  function addFiles(list: FileList | File[]) {
    const incoming = Array.from(list);
    const accepted: File[] = [];
    for (const f of incoming) {
      if (BLOCKED.test(f.name)) {
        onToast(t('support.fileBlocked', { name: f.name }));
        continue;
      }
      if (f.size > MAX_FILE_BYTES) {
        onToast(t('support.fileTooLarge', { name: f.name }));
        continue;
      }
      accepted.push(f);
    }
    setFiles((prev) => {
      const next = [...prev, ...accepted];
      if (next.length > MAX_FILES) onToast(t('support.tooManyFiles', { count: MAX_FILES }));
      return next.slice(0, MAX_FILES);
    });
  }

  function preview(f: File): string | null {
    if (!f.type.startsWith('image/')) return null;
    let url = previews.current.get(f);
    if (!url) {
      url = URL.createObjectURL(f);
      previews.current.set(f, url);
    }
    return url;
  }

  async function send() {
    const body = text.trim();
    if ((!body && files.length === 0) || sending || disabled) return;
    setSending(true);
    setProgress(files.length > 0 ? 0 : null);
    const ok = await onSend(body, files, (p) => setProgress(p));
    setSending(false);
    setProgress(null);
    if (ok) {
      setText('');
      setFiles([]);
      areaRef.current?.focus();
    }
  }

  return (
    <div
      className={`support-composer ${dragging ? 'support-composer--drag' : ''}`}
      onDragOver={(e) => {
        if (disabled) return;
        e.preventDefault();
        setDragging(true);
      }}
      onDragLeave={() => setDragging(false)}
      onDrop={(e) => {
        e.preventDefault();
        setDragging(false);
        if (!disabled && e.dataTransfer.files.length) addFiles(e.dataTransfer.files);
      }}
    >
      {files.length > 0 && (
        <div className="support-composer__files">
          {files.map((f, i) => {
            const url = preview(f);
            return (
              <div key={`${f.name}-${i}`} className="support-chip">
                {url ? <img src={url} alt={f.name} /> : <FileIcon size={16} />}
                <span className="support-chip__name">{f.name}</span>
                <button
                  type="button"
                  className="support-chip__remove"
                  onClick={() => setFiles((prev) => prev.filter((_, j) => j !== i))}
                  aria-label={t('support.removeFile')}
                >
                  <CloseIcon size={12} />
                </button>
              </div>
            );
          })}
        </div>
      )}
      {progress !== null && (
        <div className="support-composer__progress">
          <span style={{ width: `${progress}%` }} />
        </div>
      )}
      <div className="support-composer__row">
        <button
          type="button"
          className="icon-btn support-composer__attach"
          onClick={() => inputRef.current?.click()}
          disabled={disabled || sending || files.length >= MAX_FILES}
          aria-label={t('support.attach')}
          title={t('support.attach')}
        >
          <UploadIcon size={18} />
        </button>
        <input
          ref={inputRef}
          type="file"
          multiple
          hidden
          onChange={(e) => {
            if (e.target.files) addFiles(e.target.files);
            e.target.value = '';
          }}
        />
        <textarea
          ref={areaRef}
          className="support-composer__input"
          rows={1}
          value={text}
          placeholder={placeholder}
          disabled={disabled}
          onChange={(e) => setText(e.target.value)}
          onPaste={(e) => {
            const pasted = Array.from(e.clipboardData.files);
            if (pasted.length) {
              e.preventDefault();
              addFiles(pasted);
            }
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
              e.preventDefault();
              void send();
            }
          }}
        />
        <button
          type="button"
          className="icon-btn support-composer__send"
          onClick={() => void send()}
          disabled={disabled || sending || (!text.trim() && files.length === 0)}
          aria-label={t('common.send')}
        >
          <SendIcon size={18} />
        </button>
      </div>
    </div>
  );
}
