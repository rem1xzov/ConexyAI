import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { MessageAttachment } from '../types/chat';
import { loadOriginal } from '../utils/attachmentStore';
import { downloadAttachment, openAttachment } from '../utils/openAttachment';
import { ChevronDownIcon, CloseIcon, DownloadIcon, FileIcon } from './Icons';

function originalLoader(attachment: MessageAttachment): () => Promise<Blob | undefined> {
  return () => (attachment.originalId ? loadOriginal(attachment.originalId) : Promise.resolve(undefined));
}

// ATTACHMENT_GRID: добавлено 2026-09-22 — раньше каждое вложение рендерилось отдельной
// полноширинной строкой, и сообщение с 4 фото растягивалось на весь экран. Теперь раскладка
// считается по количеству картинок: 1 / 2 (в ряд) / 3-4 (2x2) / 5+ (2x2 + оверлей "+N").

/** Сколько плиток рисуется, прежде чем остальные сворачиваются в оверлей "+N". */
const MAX_TILES = 4;

interface AttachmentGridProps {
  attachments: MessageAttachment[];
}

function imageLayoutModifier(imageCount: number): string {
  if (imageCount === 1) return 'attachment-grid--single';
  if (imageCount === 2) return 'attachment-grid--duo';
  return 'attachment-grid--quad';
}

// I18N_L2: добавлено 2026-09-24 — единицы размера («B/KB/MB») переводятся (Б/КБ/МБ).
function formatSize(bytes: number | undefined, t: (key: string, options?: Record<string, unknown>) => string): string | null {
  if (!bytes || bytes <= 0) return null;
  if (bytes < 1024) return t('render.sizeBytes', { n: bytes });
  if (bytes < 1024 * 1024) return t('render.sizeKilobytes', { n: Math.round(bytes / 1024) });
  return t('render.sizeMegabytes', { n: (bytes / (1024 * 1024)).toFixed(1) });
}

interface LightboxProps {
  images: MessageAttachment[];
  index: number;
  onClose: () => void;
  onNavigate: (index: number) => void;
}

/** Minimal full-screen viewer: click a tile to open, arrows / Esc to move around. */
function Lightbox({ images, index, onClose, onNavigate }: LightboxProps) {
  const { t } = useTranslation();
  const current = images[index];
  // ATTACHMENT_ORIGINALS: во весь экран показываем оригинал, а не превью 320px; пока он читается — превью.
  const [originalUrl, setOriginalUrl] = useState<string | null>(null);
  const [missing, setMissing] = useState(false);
  const [zoomed, setZoomed] = useState(false);

  useEffect(() => {
    setOriginalUrl(null);
    setMissing(false);
    setZoomed(false);
    if (!current) return;
    let cancelled = false;
    let url: string | null = null;
    void originalLoader(current)().then((blob) => {
      if (cancelled) return;
      if (!blob) {
        setMissing(true);
        return;
      }
      url = URL.createObjectURL(blob);
      setOriginalUrl(url);
    });
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [current]);

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose();
      else if (e.key === 'ArrowLeft') onNavigate((index - 1 + images.length) % images.length);
      else if (e.key === 'ArrowRight') onNavigate((index + 1) % images.length);
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [index, images.length, onClose, onNavigate]);

  // The viewer is modal: freeze the page behind it so scrolling does not leak through.
  useEffect(() => {
    const previous = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = previous;
    };
  }, []);

  if (!current) return null;

  return (
    <div
      className="attachment-lightbox"
      role="dialog"
      aria-modal="true"
      aria-label={current.fileName}
      onClick={onClose}
    >
      <button
        type="button"
        className="attachment-lightbox__close"
        onClick={onClose}
        aria-label={t('attachment.close')}
      >
        <CloseIcon />
      </button>

      {originalUrl && (
        <button
          type="button"
          className="attachment-lightbox__download"
          onClick={(e) => {
            e.stopPropagation();
            void downloadAttachment(current.fileName, current.contentType, originalLoader(current));
          }}
          aria-label={t('attachment.download')}
          title={t('attachment.download')}
        >
          <DownloadIcon />
        </button>
      )}

      <div
        className={`attachment-lightbox__stage${zoomed ? ' attachment-lightbox__stage--zoomed' : ''}`}
        onClick={zoomed ? (e) => e.stopPropagation() : undefined}
      >
        <img
          className="attachment-lightbox__img"
          src={originalUrl ?? current.previewUrl}
          alt={current.fileName}
          title={originalUrl ? (zoomed ? t('attachment.zoomOut') : t('attachment.zoomIn')) : undefined}
          style={{ cursor: originalUrl ? (zoomed ? 'zoom-out' : 'zoom-in') : 'default' }}
          onClick={(e) => {
            e.stopPropagation();
            if (originalUrl) setZoomed((z) => !z);
          }}
        />
      </div>

      {missing && <div className="attachment-lightbox__note">{t('attachment.previewOnly')}</div>}

      {images.length > 1 && (
        <>
          <button
            type="button"
            className="attachment-lightbox__nav attachment-lightbox__nav--prev"
            onClick={(e) => {
              e.stopPropagation();
              onNavigate((index - 1 + images.length) % images.length);
            }}
            aria-label={t('attachment.prev')}
          >
            <ChevronDownIcon className="rotate-90" />
          </button>
          <button
            type="button"
            className="attachment-lightbox__nav attachment-lightbox__nav--next"
            onClick={(e) => {
              e.stopPropagation();
              onNavigate((index + 1) % images.length);
            }}
            aria-label={t('attachment.next')}
          >
            <ChevronDownIcon className="-rotate-90" />
          </button>
          <div className="attachment-lightbox__counter">
            {t('attachment.counter', { index: index + 1, total: images.length })}
          </div>
        </>
      )}
    </div>
  );
}

export function AttachmentGrid({ attachments }: AttachmentGridProps) {
  const { t } = useTranslation();
  const [lightboxIndex, setLightboxIndex] = useState<number | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);

  async function openFile(file: MessageAttachment) {
    setFileError(null);
    if (!file.originalId) {
      setFileError(t('attachment.unavailable'));
      return;
    }
    const ok = await openAttachment(file.fileName, file.contentType, originalLoader(file));
    if (!ok) setFileError(t('attachment.unavailable'));
  }

  const images = attachments.filter((a) => Boolean(a.previewUrl));
  const files = attachments.filter((a) => !a.previewUrl);

  if (attachments.length === 0) return null;

  const tiles = images.slice(0, MAX_TILES);
  const hiddenCount = images.length - tiles.length;

  return (
    <>
      {tiles.length > 0 && (
        <div className={`attachment-grid ${imageLayoutModifier(tiles.length)}`}>
          {tiles.map((image, i) => {
            const isLastTile = i === tiles.length - 1;
            const showOverlay = isLastTile && hiddenCount > 0;
            return (
              <button
                type="button"
                key={`${image.fileName}-${i}`}
                className="attachment-grid__cell"
                title={image.fileName}
                aria-label={t('attachment.view')}
                onClick={() => setLightboxIndex(i)}
              >
                <img className="attachment-grid__img" src={image.previewUrl} alt={image.fileName} />
                {showOverlay && (
                  <span className="attachment-grid__more">+{hiddenCount}</span>
                )}
              </button>
            );
          })}
        </div>
      )}

      {files.length > 0 && (
        <div className="attachment-files">
          {files.map((file, i) => {
            const size = formatSize(file.sizeBytes, t);
            return (
              <button
                type="button"
                key={`${file.fileName}-${i}`}
                className="attachment-files__chip"
                title={file.fileName}
                aria-label={t('attachment.openFile', { name: file.fileName })}
                onClick={() => void openFile(file)}
              >
                <FileIcon size={14} />
                <span className="attachment-files__name">{file.fileName}</span>
                {size && <span className="attachment-files__size">{size}</span>}
              </button>
            );
          })}
        </div>
      )}
      {fileError && <div className="attachment-files__error" role="status">{fileError}</div>}

      {lightboxIndex !== null && images.length > 0 && (
        <Lightbox
          images={images}
          index={Math.min(lightboxIndex, images.length - 1)}
          onClose={() => setLightboxIndex(null)}
          onNavigate={setLightboxIndex}
        />
      )}
    </>
  );
}
