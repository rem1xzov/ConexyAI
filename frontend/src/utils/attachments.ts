import type { TaskAttachment } from '../types/api';
import type { MessageAttachment } from '../types/chat';

// BUGFIX_ATTACHMENTS: добавлено 2026-09-21
// The old whitelist accepted images and plain text-ish MIME types only, so documents (.docx,
// .pdf, …) were dropped without any feedback, in every tab. It now mirrors what the model and the
// agent workspace can actually consume, and falls back to the file extension because browsers
// report inconsistent types (an empty type, or a generic application/octet-stream).
const ALLOWED_MIME_PREFIXES = ['image/', 'text/', 'audio/', 'video/'];

const ALLOWED_MIME_EXACT = new Set([
  'application/json',
  'application/xml',
  'application/yaml',
  'application/pdf',
  'application/rtf',
  'application/msword',
  'application/vnd.ms-excel',
  'application/vnd.ms-powerpoint',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  'application/vnd.openxmlformats-officedocument.presentationml.presentation',
  'application/vnd.oasis.opendocument.text',
  'application/zip',
  'application/x-zip-compressed',
  'application/octet-stream',
]);

const ALLOWED_EXTENSIONS = new Set([
  'png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp', 'svg',
  'txt', 'md', 'csv', 'json', 'xml', 'yaml', 'yml', 'log',
  'pdf', 'doc', 'docx', 'rtf', 'odt', 'xls', 'xlsx', 'ppt', 'pptx', 'pages',
  'zip',
]);

function extensionOf(fileName?: string): string {
  if (!fileName) return '';
  const dot = fileName.lastIndexOf('.');
  return dot < 0 ? '' : fileName.slice(dot + 1).toLowerCase();
}

/** A file is accepted on its declared type, or — for generic/unknown types — on its extension. */
export function isAllowedMime(contentType: string, fileName?: string): boolean {
  const type = (contentType || '').toLowerCase();
  if (ALLOWED_MIME_PREFIXES.some((p) => type.startsWith(p))) return true;
  if (type && !type.startsWith('application/octet-stream') && ALLOWED_MIME_EXACT.has(type)) return true;
  return ALLOWED_EXTENSIONS.has(extensionOf(fileName));
}

export function fileToAttachment(file: File): Promise<TaskAttachment> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const result = reader.result as string; // "data:<mime>;base64,<data>"
      const comma = result.indexOf(',');
      resolve({
        fileName: file.name,
        contentBase64: comma >= 0 ? result.slice(comma + 1) : result,
        contentType: file.type || 'application/octet-stream',
      });
    };
    reader.onerror = () => reject(reader.error ?? new Error('Failed to read file'));
    reader.readAsDataURL(file);
  });
}

const THUMBNAIL_MAX_EDGE = 320;

/**
 * Downscales an image into a small JPEG data URL for the message bubble.
 *
 * History lives in localStorage, so keeping the full base64 of every photo would blow the quota
 * and silently drop the whole session list. The model still receives the original bytes; this is
 * only what the transcript shows.
 */
export function imageThumbnailDataUrl(contentType: string, base64: string): Promise<string | undefined> {
  return new Promise((resolve) => {
    const img = new Image();
    img.onload = () => {
      try {
        const scale = Math.min(1, THUMBNAIL_MAX_EDGE / Math.max(img.width || 1, img.height || 1));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round((img.width || 1) * scale));
        canvas.height = Math.max(1, Math.round((img.height || 1) * scale));
        const ctx = canvas.getContext('2d');
        if (!ctx) {
          resolve(undefined);
          return;
        }
        ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
        resolve(canvas.toDataURL('image/jpeg', 0.72));
      } catch {
        resolve(undefined);
      }
    };
    img.onerror = () => resolve(undefined);
    img.src = `data:${contentType};base64,${base64}`;
  });
}

/** Builds the transcript-side representation of an attachment sent to the model. */
export async function toMessageAttachment(attachment: TaskAttachment): Promise<MessageAttachment> {
  const base: MessageAttachment = {
    fileName: attachment.fileName,
    contentType: attachment.contentType,
    sizeBytes: Math.round((attachment.contentBase64.length * 3) / 4),
  };

  if (!attachment.contentType.toLowerCase().startsWith('image/')) return base;

  const previewUrl = await imageThumbnailDataUrl(attachment.contentType, attachment.contentBase64);
  return previewUrl ? { ...base, previewUrl } : base;
}

const MIME_TO_EXT: Record<string, string> = {
  'image/png': 'png',
  'image/jpeg': 'jpg',
  'image/webp': 'webp',
  'image/gif': 'gif',
  'image/bmp': 'bmp',
  'image/svg+xml': 'svg',
};

/**
 * Clipboard images rarely carry a meaningful name, so give the pasted file a stable
 * generated name (extension derived from its actual MIME type).
 */
export function pastedImageFile(file: File): File {
  const ext = MIME_TO_EXT[file.type] ?? 'png';
  return new File([file], `screenshot-${Date.now()}.${ext}`, {
    type: file.type || 'image/png',
  });
}
