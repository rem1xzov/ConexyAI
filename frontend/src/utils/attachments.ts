import type { TaskAttachment } from '../types/api';

const ALLOWED_PREFIXES = ['image/', 'text/', 'application/json', 'application/xml', 'application/yaml'];

export function isAllowedMime(contentType: string): boolean {
  return ALLOWED_PREFIXES.some((p) => contentType.startsWith(p));
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
