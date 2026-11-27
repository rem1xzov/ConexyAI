import type { TaskAttachment } from '../types/api';
import type { MessageAttachment } from '../types/chat';
import { base64ToBlob, loadOriginal, saveOriginal } from './attachmentStore';

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

// ATTACHMENT_SIZE_LIMIT: изменено 2026-09-24 (M24)
// Вложения едут внутри JSON в base64 (+1/3 к размеру). Провайдер модели принимает тело запроса
// до 48 MiB, а сервер — до 55 000 000 байт; клиент держит себя ниже обоих лимитов с запасом.
/** Raw (not base64) bytes of every file of one message together. */
export const MAX_TOTAL_ATTACHMENT_BYTES = 34_000_000;
/** Estimated request body the client allows itself — safely below the provider's 48 MiB. */
export const MAX_REQUEST_BYTES = 48_000_000;

// IMAGE_NORMALIZATION: добавлено 2026-11-27 — модель принимает JPEG, PNG, GIF и WebP, определяя формат
// по содержимому, а провайдер ограничивает тело запроса. Поэтому большие фото ужимаются на клиенте, а
// формат/тип берутся из сигнатуры файла, а не из необязательного `file.type` браузера.
/** Photos above this raw size are downscaled so a batch of them still fits the provider limits. */
const IMAGE_OPTIMIZE_ABOVE_BYTES = 4_000_000;
/** Longest edge of an image handed to the model; bigger photos lose no useful model detail. */
const IMAGE_MAX_EDGE = 2048;
const JPEG_QUALITY = 0.85;
/** Formats the model accepts by content (see DeepSeek vision limits). */
const MODEL_IMAGE_MIMES = new Set(['image/png', 'image/jpeg', 'image/gif', 'image/webp']);

/** Raw size of the attached files (decoded from their base64 length). */
export function attachmentBytes(attachments: TaskAttachment[]): number {
  return attachments.reduce((sum, a) => sum + Math.floor((a.contentBase64.length * 3) / 4), 0);
}

/**
 * Upper estimate of the JSON body of a run request: base64 as is, file names, the prompt at the
 * worst-case UTF-8/escaping cost, and a fixed allowance for the remaining fields.
 */
export function estimateRequestBytes(attachments: TaskAttachment[], prompt: string): number {
  const files = attachments.reduce(
    (sum, a) => sum + a.contentBase64.length + a.fileName.length * 6 + a.contentType.length + 64,
    0,
  );
  return files + prompt.length * 6 + 4_096;
}

/** "12.3" — megabytes with one decimal, as shown in the size messages. */
export function formatMegabytes(bytes: number): string {
  return (bytes / 1_000_000).toFixed(1).replace(/\.0$/, '');
}

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

export function fileToAttachment(
  file: File,
  // ATTACHMENT_PROGRESS: доля прочитанного (0..1) — по ней композер рисует ползунок загрузки.
  onProgress?: (fraction: number) => void,
): Promise<TaskAttachment> {
  return readFileAsDataUrl(file, onProgress).then((dataUrl) => {
    const comma = dataUrl.indexOf(',');
    const base64 = comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl;
    onProgress?.(1);
    return prepareAttachment(file.name, base64, declaredMime(file));
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

/**
 * Builds the transcript-side representation of an attachment sent to the model.
 * ATTACHMENT_ORIGINALS: the original bytes are kept aside (see attachmentStore) so the sender can
 * open the photo in full quality or the file itself; `persist: false` keeps them in memory only.
 */
export async function toMessageAttachment(
  attachment: TaskAttachment,
  persist = true,
): Promise<MessageAttachment> {
  const base: MessageAttachment = {
    fileName: attachment.fileName,
    contentType: attachment.contentType,
    sizeBytes: Math.round((attachment.contentBase64.length * 3) / 4),
  };

  try {
    const originalId = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
    await saveOriginal(originalId, base64ToBlob(attachment.contentBase64, attachment.contentType), persist);
    base.originalId = originalId;
  } catch (e) {
    console.warn('[Attachments] could not keep the original', String(e));
  }

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
  'application/pdf': 'pdf',
  'application/rtf': 'rtf',
  'application/msword': 'doc',
  'application/vnd.ms-excel': 'xls',
  'application/vnd.ms-powerpoint': 'ppt',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': 'docx',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': 'xlsx',
  'application/vnd.openxmlformats-officedocument.presentationml.presentation': 'pptx',
  'application/vnd.oasis.opendocument.text': 'odt',
  'text/plain': 'txt',
  'text/markdown': 'md',
  'text/csv': 'csv',
  'application/json': 'json',
  'application/xml': 'xml',
  'application/zip': 'zip',
};

const EXT_TO_MIME: Record<string, string> = {
  png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', webp: 'image/webp', gif: 'image/gif',
  bmp: 'image/bmp', svg: 'image/svg+xml',
  pdf: 'application/pdf', rtf: 'application/rtf', doc: 'application/msword', docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  xls: 'application/vnd.ms-excel', xlsx: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  ppt: 'application/vnd.ms-powerpoint', pptx: 'application/vnd.openxmlformats-officedocument.presentationml.presentation',
  odt: 'application/vnd.oasis.opendocument.text',
  txt: 'text/plain', md: 'text/markdown', csv: 'text/csv', json: 'application/json', xml: 'application/xml',
  yaml: 'application/yaml', yml: 'application/yaml', log: 'text/plain', zip: 'application/zip',
};

/**
 * An image the browser/our encoder cannot turn into a format the model accepts (e.g. HEIC on
 * Chromium, a corrupt file). The composer reports it by name instead of silently dropping it.
 */
export class UnsupportedImageError extends Error {
  constructor(readonly fileName: string) {
    super(`Unsupported image: ${fileName}`);
    this.name = 'UnsupportedImageError';
  }
}

/** `file.type` when the browser actually knows it, otherwise the type implied by the extension. */
function declaredMime(file: File): string {
  const type = (file.type || '').trim().toLowerCase();
  if (type && type !== 'application/octet-stream') return type;
  return EXT_TO_MIME[extensionOf(file.name)] ?? 'application/octet-stream';
}

function readFileAsDataUrl(file: File, onProgress?: (fraction: number) => void): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onprogress = (e) => {
      if (onProgress && e.lengthComputable && e.total > 0) {
        onProgress(Math.min(1, e.loaded / e.total));
      }
    };
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(reader.error ?? new Error('Failed to read file'));
    reader.readAsDataURL(file);
  });
}

function base64Size(base64: string): number {
  return Math.floor((base64.length * 3) / 4);
}

/** MIME по первым байтам base64 (сигнатура), без декодирования всего файла. */
function sniffImageMime(base64: string): string | undefined {
  let prefix: string;
  try {
    const slice = base64.length > 64 ? base64.slice(0, 64) : base64;
    prefix = atob(slice);
  } catch {
    return undefined;
  }
  const bytes = Array.from(prefix, (c) => c.charCodeAt(0));
  const at = (i: number) => bytes[i] ?? -1;
  if (at(0) === 0x89 && at(1) === 0x50 && at(2) === 0x4e && at(3) === 0x47) return 'image/png';
  if (at(0) === 0xff && at(1) === 0xd8 && at(2) === 0xff) return 'image/jpeg';
  if (at(0) === 0x47 && at(1) === 0x49 && at(2) === 0x46 && at(3) === 0x38) return 'image/gif';
  if (at(0) === 0x42 && at(1) === 0x4d) return 'image/bmp';
  if (
    at(0) === 0x52 && at(1) === 0x49 && at(2) === 0x46 && at(3) === 0x46 &&
    at(8) === 0x57 && at(9) === 0x45 && at(10) === 0x42 && at(11) === 0x50
  ) {
    return 'image/webp';
  }
  return undefined;
}

function renameToMime(fileName: string, mime: string): string {
  const ext = MIME_TO_EXT[mime];
  if (!ext) return fileName;
  const dot = fileName.lastIndexOf('.');
  return `${dot > 0 ? fileName.slice(0, dot) : fileName}.${ext}`;
}

function loadImage(dataUrl: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error('Image could not be decoded'));
    img.src = dataUrl;
  });
}

/** Re-encodes an image into a provider-supported format, downscaling it when it is large. */
async function transcodeImage(img: HTMLImageElement, sourceMime: string): Promise<{ mime: string; base64: string }> {
  const scale = Math.min(1, IMAGE_MAX_EDGE / Math.max(img.width || 1, img.height || 1));
  const width = Math.max(1, Math.round((img.width || 1) * scale));
  const height = Math.max(1, Math.round((img.height || 1) * scale));
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('Canvas is unavailable');

  // PNG keeps transparency and crisp UI text; photos become smaller JPEGs on a white background.
  const mime = sourceMime === 'image/png' ? 'image/png' : 'image/jpeg';
  if (mime === 'image/jpeg') {
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, width, height);
  }
  ctx.drawImage(img, 0, 0, width, height);

  const dataUrl = canvas.toDataURL(mime, JPEG_QUALITY);
  const comma = dataUrl.indexOf(',');
  return { mime, base64: comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl };
}

/**
 * Makes one attachment safe for the model: images get a content-derived type, and oversized or
 * unsupported images are re-encoded into JPEG/PNG. Everything else keeps its bytes untouched.
 */
async function prepareAttachment(fileName: string, base64: string, declared: string): Promise<TaskAttachment> {
  const detected = sniffImageMime(base64);
  const looksLikeImage = detected !== undefined || declared.startsWith('image/');
  if (!looksLikeImage) {
    return { fileName, contentBase64: base64, contentType: declared };
  }

  const sourceMime = detected ?? declared;
  if (sourceMime === 'image/gif') {
    // Animated GIFs are accepted by the model as-is; re-encoding would lose the animation.
    return { fileName: renameToMime(fileName, 'image/gif'), contentBase64: base64, contentType: 'image/gif' };
  }

  const smallEnough = base64Size(base64) <= IMAGE_OPTIMIZE_ABOVE_BYTES;
  if (MODEL_IMAGE_MIMES.has(sourceMime) && smallEnough) {
    return { fileName: renameToMime(fileName, sourceMime), contentBase64: base64, contentType: sourceMime };
  }

  let img: HTMLImageElement;
  try {
    img = await loadImage(`data:${sourceMime};base64,${base64}`);
  } catch {
    // The browser cannot decode this image (HEIC on Chromium, a corrupt file, SVG where content
    // sniffing failed). Say so by name instead of sending a payload the model will reject.
    throw new UnsupportedImageError(fileName);
  }

  const encoded = await transcodeImage(img, sourceMime);
  return {
    fileName: renameToMime(fileName, encoded.mime),
    contentBase64: encoded.base64,
    contentType: encoded.mime,
  };
}

/**
 * Rebuilds the attachments of an already sent message from this device's original store.
 *
 * TURN_REPLAY: "сгенерировать заново", "продолжить" и правка сообщения пересобирают ход с тем же
 * текстом, и раньше они отправляли пустой список вложений — модель внезапно «переставала видеть»
 * фото, которые пользователь уже прикладывал. Оригиналы лежат в attachmentStore; если их нет на
 * этом устройстве, ход просто пойдёт без картинок, как раньше.
 */
export async function attachmentsFromMessage(message: {
  attachments?: MessageAttachment[];
}): Promise<TaskAttachment[]> {
  const saved = message.attachments ?? [];
  if (saved.length === 0) return [];

  const restored: TaskAttachment[] = [];
  for (const attachment of saved) {
    if (!attachment.originalId) continue;
    const blob = await loadOriginal(attachment.originalId);
    if (!blob) continue;
    try {
      const file = new File([blob], attachment.fileName, { type: attachment.contentType });
      restored.push(await fileToAttachment(file));
    } catch (e) {
      console.warn('[Attachments] could not restore an attachment for the turn', attachment.fileName, String(e));
    }
  }
  return restored;
}

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
