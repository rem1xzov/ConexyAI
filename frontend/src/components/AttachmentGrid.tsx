import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { MessageAttachment } from '../types/chat';
import { ChevronDownIcon, CloseIcon, FileIcon } from './Icons';

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

      <img
        className="attachment-lightbox__img"
        src={current.previewUrl}
        alt={current.fileName}
        onClick={(e) => e.stopPropagation()}
      />

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
              <span key={`${file.fileName}-${i}`} className="attachment-files__chip" title={file.fileName}>
                <FileIcon size={14} />
                <span className="attachment-files__name">{file.fileName}</span>
                {size && <span className="attachment-files__size">{size}</span>}
              </span>
            );
          })}
        </div>
      )}

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
