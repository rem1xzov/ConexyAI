/**
 * How long the object URL outlives the click. Revoking it synchronously right after `a.click()`
 * can cancel the download in Safari / iOS, which start reading the blob asynchronously
 * (review L5); 40 s is the delay FileSaver.js settled on.
 */
const REVOKE_DELAY_MS = 40_000;

/** Saves a blob through a temporary link (the browser's regular download flow). */
export function triggerDownload(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.rel = 'noopener';
  document.body.appendChild(a);
  a.click();
  a.remove();
  // DOWNLOAD_REVOKE: добавлено 2026-09-24 — отзываем URL с задержкой, а не сразу после клика.
  window.setTimeout(() => URL.revokeObjectURL(url), REVOKE_DELAY_MS);
}
