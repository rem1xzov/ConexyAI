// ATTACHMENT_ORIGINALS: открыть отправленный пользователем файл. В вебе безопасные для показа типы
// открываются во вкладке, остальные скачиваются; в Android-приложении файл уходит в системное
// приложение через нативный плагин FileOpener (WebView не умеет скачивать blob-ссылки).
import { triggerDownload } from './download';
import { getNativePlugin } from './mobile';

interface FileOpener {
  open: (options: { fileName: string; contentType: string; base64: string }) => Promise<{ opened: boolean }>;
}

/**
 * Что можно показать прямо во вкладке. HTML/SVG/XML сюда не входят: blob-URL живёт в origin сайта,
 * и скрипт из такого файла получил бы доступ к сессии. Текст принудительно идёт как text/plain.
 */
function inlineType(contentType: string): string | null {
  const type = contentType.toLowerCase();
  if (type === 'application/pdf') return type;
  if (type.startsWith('image/') && type !== 'image/svg+xml') return type;
  if (type.startsWith('audio/') || type.startsWith('video/')) return type;
  if (type.startsWith('text/') || type === 'application/json') return 'text/plain;charset=utf-8';
  return null;
}

function blobToBase64(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const result = String(reader.result ?? '');
      const comma = result.indexOf(',');
      resolve(comma >= 0 ? result.slice(comma + 1) : result);
    };
    reader.onerror = () => reject(reader.error ?? new Error('Failed to read file'));
    reader.readAsDataURL(blob);
  });
}

/**
 * Открывает оригинал. `load` вызывается уже после того, как (в вебе) открыта пустая вкладка —
 * иначе браузер заблокировал бы окно, открытое после await, как всплывающее.
 */
export async function openAttachment(
  fileName: string,
  contentType: string,
  load: () => Promise<Blob | undefined>,
): Promise<boolean> {
  const opener = getNativePlugin<FileOpener>('FileOpener');
  if (opener) {
    const blob = await load();
    if (!blob) return false;
    try {
      await opener.open({ fileName, contentType: contentType || blob.type, base64: await blobToBase64(blob) });
      return true;
    } catch {
      return false;
    }
  }

  const inline = inlineType(contentType);
  const tab = inline ? window.open('', '_blank') : null;
  const blob = await load();
  if (!blob) {
    tab?.close();
    return false;
  }
  if (inline && tab) {
    const url = URL.createObjectURL(new Blob([blob], { type: inline }));
    tab.opener = null;
    tab.location.href = url;
    window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
    return true;
  }
  triggerDownload(blob, fileName);
  return true;
}

/** Скачивание оригинала (в приложении — тоже через системное приложение). */
export async function downloadAttachment(
  fileName: string,
  contentType: string,
  load: () => Promise<Blob | undefined>,
): Promise<boolean> {
  const opener = getNativePlugin<FileOpener>('FileOpener');
  if (opener) return openAttachment(fileName, contentType, load);
  const blob = await load();
  if (!blob) return false;
  triggerDownload(blob, fileName);
  return true;
}
