import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { ShareToAdminRequest } from '../types/api';
import type { ChatSession } from '../types/chat';
import { fileToAttachment, formatMegabytes, isAllowedMime } from '../utils/attachments';
import { DialogShell } from './Dialog';
import { PhotoIcon } from './Icons';

// SHARE_ADMIN: границы совпадают с контрактом бэкенда, чтобы не отправлять заведомо отклоняемый
// запрос: комментарий ≤ 2000 символов, скриншот ≤ 8 МБ, только png/jpeg/webp.
const MAX_COMMENT_LENGTH = 2000;
// Считаем по десятичной шкале — так же, как formatMegabytes в attachments.ts.
const MAX_SCREENSHOT_BYTES = 8_000_000;
const ALLOWED_SCREENSHOT_TYPES = ['image/png', 'image/jpeg', 'image/webp'];
const EXTENSION_TYPES: Record<string, string> = {
  png: 'image/png',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  webp: 'image/webp',
};

/**
 * Браузеры не всегда отдают MIME картинки (а на Android бывает application/octet-stream), поэтому
 * тип восстанавливаем по расширению и всё равно ограничиваем тремя форматами, которые ждёт сервер.
 */
function resolveScreenshotType(file: File): string | null {
  const declared = (file.type || '').toLowerCase();
  if (ALLOWED_SCREENSHOT_TYPES.includes(declared)) return declared;
  const extension = file.name.includes('.') ? file.name.split('.').pop()?.toLowerCase() ?? '' : '';
  return EXTENSION_TYPES[extension] ?? null;
}

interface AttachedScreenshot {
  fileName: string;
  contentType: string;
  base64: string;
  previewUrl: string;
}

interface ShareWithAdminDialogProps {
  session: ChatSession;
  busy?: boolean;
  onSubmit: (request: ShareToAdminRequest) => void;
  onCancel: () => void;
}

/** Owner-side dialog: send this chat to the admin with an optional comment and screenshot. */
export function ShareWithAdminDialog({ session, busy, onSubmit, onCancel }: ShareWithAdminDialogProps) {
  const { t } = useTranslation();
  const titleId = useId();
  const fileRef = useRef<HTMLInputElement>(null);
  // Object URL превью надо освобождать: иначе каждый выбранный скриншот остаётся в памяти до reload.
  const previewUrlRef = useRef<string | null>(null);
  // Диалог могли закрыть, пока FileReader ещё читал файл — тогда созданное превью нужно сразу убрать.
  const mountedRef = useRef(true);
  const [comment, setComment] = useState('');
  const [screenshot, setScreenshot] = useState<AttachedScreenshot | null>(null);
  const [reading, setReading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    return () => {
      mountedRef.current = false;
      if (previewUrlRef.current) URL.revokeObjectURL(previewUrlRef.current);
      previewUrlRef.current = null;
    };
  }, []);

  function clearScreenshot() {
    if (previewUrlRef.current) URL.revokeObjectURL(previewUrlRef.current);
    previewUrlRef.current = null;
    setScreenshot(null);
  }

  function openPicker() {
    if (busy || reading) return;
    fileRef.current?.click();
  }

  async function readScreenshot(file: File) {
    setError(null);
    const contentType = resolveScreenshotType(file);
    // isAllowedMime отсекает не-картинки по расширению (например .txt, переименованный в .png).
    if (!contentType || !isAllowedMime(file.type, file.name)) {
      setError(t('shareAdmin.badType'));
      return;
    }
    if (file.size > MAX_SCREENSHOT_BYTES) {
      setError(t('shareAdmin.tooLarge', { limit: formatMegabytes(MAX_SCREENSHOT_BYTES) }));
      return;
    }

    setReading(true);
    try {
      const attachment = await fileToAttachment(file);
      const previewUrl = URL.createObjectURL(file);
      if (!mountedRef.current) {
        URL.revokeObjectURL(previewUrl);
        return;
      }
      if (previewUrlRef.current) URL.revokeObjectURL(previewUrlRef.current);
      previewUrlRef.current = previewUrl;
      setScreenshot({
        fileName: attachment.fileName,
        contentType,
        base64: attachment.contentBase64,
        previewUrl,
      });
    } catch {
      if (mountedRef.current) setError(t('shareAdmin.readError'));
    } finally {
      if (mountedRef.current) setReading(false);
    }
  }

  function submit() {
    if (busy || reading) return;
    const request: ShareToAdminRequest = {};
    if (comment.trim()) request.comment = comment.trim();
    if (screenshot) {
      request.screenshotFileName = screenshot.fileName;
      request.screenshotContentType = screenshot.contentType;
      request.screenshotBase64 = screenshot.base64;
    }
    onSubmit(request);
  }

  return (
    <DialogShell onCancel={busy ? () => {} : onCancel} labelledBy={titleId}>
      <div className="dialog-title" id={titleId}>
        {t('shareAdmin.title')}
      </div>
      <div className="dialog-message">{t('shareAdmin.hint')}</div>

      <div className="share-admin__chat" title={session.title}>
        <span className="share-admin__chat-label">{t('shareAdmin.chat')}</span>
        <span className="share-admin__chat-title">{session.title || t('share.untitled')}</span>
      </div>

      <label className="dialog-field">
        <span className="dialog-field__label">{t('shareAdmin.commentLabel')}</span>
        <textarea
          className="dialog-input dialog-textarea"
          value={comment}
          placeholder={t('shareAdmin.commentPlaceholder')}
          maxLength={MAX_COMMENT_LENGTH}
          rows={4}
          onChange={(e) => setComment(e.target.value)}
          disabled={busy}
        />
        <span className="dialog-counter">
          {comment.length} / {MAX_COMMENT_LENGTH}
        </span>
      </label>

      <div className="dialog-field">
        <span className="dialog-field__label">{t('shareAdmin.screenshotLabel')}</span>

        {screenshot ? (
          <div className="share-admin__shot">
            <img src={screenshot.previewUrl} alt={t('shareAdmin.screenshotAlt')} />
            <div className="share-admin__shot-actions">
              <button className="dialog-btn" type="button" onClick={openPicker} disabled={busy || reading}>
                {t('shareAdmin.screenshotReplace')}
              </button>
              <button className="dialog-btn" type="button" onClick={clearScreenshot} disabled={busy || reading}>
                {t('shareAdmin.screenshotRemove')}
              </button>
            </div>
          </div>
        ) : (
          <button
            className="dialog-btn share-admin__shot-pick"
            type="button"
            onClick={openPicker}
            disabled={busy || reading}
          >
            <PhotoIcon size={16} /> {reading ? t('common.loading') : t('shareAdmin.screenshotPick')}
          </button>
        )}

        <span className="dialog-hint">
          {t('shareAdmin.screenshotHint', { limit: formatMegabytes(MAX_SCREENSHOT_BYTES) })}
        </span>

        {/* MOBILE_CAPTURE: на телефоне та же кнопка открывает камеру (capture), на ПК — выбор файла. */}
        <input
          ref={fileRef}
          className="share-admin__file"
          type="file"
          accept="image/*"
          capture="environment"
          hidden
          onChange={(e) => {
            const file = e.target.files?.[0];
            // Сбрасываем значение: тот же файл можно выбрать повторно после «Убрать».
            e.target.value = '';
            if (file) void readScreenshot(file);
          }}
        />
      </div>

      {error && <div className="dialog-error">{error}</div>}

      <div className="dialog-actions">
        <button className="dialog-btn" onClick={onCancel} type="button" disabled={busy}>
          {t('common.cancel')}
        </button>
        <button
          className="dialog-btn dialog-btn--primary"
          onClick={submit}
          type="button"
          disabled={busy || reading}
        >
          {busy ? t('shareAdmin.sending') : t('shareAdmin.submit')}
        </button>
      </div>
    </DialogShell>
  );
}
