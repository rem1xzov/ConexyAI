// ATTACHMENT_ORIGINALS: оригиналы отправленных файлов, чтобы человек мог открыть своё фото в исходном
// качестве и свой документ. История чатов живёт в localStorage, и там хранится только превью 320px
// (полный base64 переполнил бы квоту), поэтому сами байты лежат отдельно: в памяти вкладки и в
// IndexedDB (квота у неё на порядки больше). Инкогнито — только в памяти, на диск не пишется.

const DB_NAME = 'conexy-attachments';
const STORE = 'originals';
/** Оригиналы старше этого срока удаляются при открытии базы, чтобы она не росла бесконечно. */
const TTL_MS = 90 * 24 * 60 * 60 * 1000;

interface StoredOriginal {
  id: string;
  blob: Blob;
  savedAt: number;
}

const memory = new Map<string, Blob>();
let dbPromise: Promise<IDBDatabase | null> | null = null;

function requestToPromise<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function pruneExpired(db: IDBDatabase): void {
  try {
    const store = db.transaction(STORE, 'readwrite').objectStore(STORE);
    const cursorRequest = store.index('savedAt').openCursor(IDBKeyRange.upperBound(Date.now() - TTL_MS));
    cursorRequest.onsuccess = () => {
      const cursor = cursorRequest.result;
      if (!cursor) return;
      cursor.delete();
      cursor.continue();
    };
  } catch {
    // Очистка — best effort.
  }
}

function openDb(): Promise<IDBDatabase | null> {
  if (dbPromise) return dbPromise;
  dbPromise = new Promise((resolve) => {
    if (typeof indexedDB === 'undefined') {
      resolve(null);
      return;
    }
    try {
      const request = indexedDB.open(DB_NAME, 1);
      request.onupgradeneeded = () => {
        const store = request.result.createObjectStore(STORE, { keyPath: 'id' });
        store.createIndex('savedAt', 'savedAt');
      };
      request.onsuccess = () => {
        pruneExpired(request.result);
        resolve(request.result);
      };
      request.onerror = () => resolve(null);
      request.onblocked = () => resolve(null);
    } catch {
      resolve(null);
    }
  });
  return dbPromise;
}

export function base64ToBlob(base64: string, contentType: string): Blob {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return new Blob([bytes], { type: contentType || 'application/octet-stream' });
}

/** Сохраняет оригинал; `persist: false` (инкогнито) — только в памяти текущей вкладки. */
export async function saveOriginal(id: string, blob: Blob, persist: boolean): Promise<void> {
  memory.set(id, blob);
  if (!persist) return;
  const db = await openDb();
  if (!db) return;
  try {
    const store = db.transaction(STORE, 'readwrite').objectStore(STORE);
    await requestToPromise(store.put({ id, blob, savedAt: Date.now() } satisfies StoredOriginal));
  } catch (e) {
    // Нет места или приватный режим браузера — файл останется доступен до перезагрузки вкладки.
    console.warn('[Attachments] could not persist the original', String(e));
  }
}

/** Оригинал по id или undefined, если его нет на этом устройстве. */
export async function loadOriginal(id: string): Promise<Blob | undefined> {
  const cached = memory.get(id);
  if (cached) return cached;
  const db = await openDb();
  if (!db) return undefined;
  try {
    const store = db.transaction(STORE, 'readonly').objectStore(STORE);
    const row = (await requestToPromise(store.get(id))) as StoredOriginal | undefined;
    if (row?.blob) memory.set(id, row.blob);
    return row?.blob;
  } catch {
    return undefined;
  }
}
