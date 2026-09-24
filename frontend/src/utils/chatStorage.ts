import type { ChatMessage, ChatSession } from '../types/chat';
import { lastActiveAt, normalizeSession } from './chatSession';

// CHAT_STORAGE: добавлено 2026-09-24 (H10, M22, L13)
// Раньше все чаты лежали ОДНИМ JSON-массивом под ключом `conexy_sessions`:
//  * без привязки к пользователю — следующий аккаунт в этом браузере видел чужие чаты (H10);
//  * вместе со скриншотами в base64 — квота переполнялась, а ошибка молча глоталась, после чего
//    НИ ОДНО сохранение больше не проходило (M22);
//  * массив целиком переписывался на каждый токен стрима;
//  * две вкладки затирали изменения друг друга, каждая записывая свой снимок всего списка (L13).
// Теперь: ключ на каждый чат `conexy_chat:<userId>:<chatId>`, запись только изменённых чатов не
// чаще раза в 500 мс, картинки в хранилище не попадают, при переполнении вытесняются кеши
// сообщений самых старых серверных чатов (сервер — источник правды), а вкладки подхватывают
// изменения друг друга через событие `storage`.
// Все ключи начинаются с `conexy_`, поэтому «Сбросить сессию» в ErrorBoundary их тоже чистит.

/** The pre-2026-09-24 storage: every chat of whoever used this browser, in one array. */
const LEGACY_KEY = 'conexy_sessions';
const CHAT_PREFIX = 'conexy_chat:';
const SAVE_INTERVAL_MS = 500;

function userPrefix(userId: string): string {
  return `${CHAT_PREFIX}${userId}:`;
}

function chatKey(userId: string, chatId: string): string {
  return `${userPrefix(userId)}${chatId}`;
}

/** The chat id stored under `key` when that key belongs to `userId`, otherwise null. */
export function chatIdFromStorageKey(key: string | null, userId: string): string | null {
  if (!key) return null;
  const prefix = userPrefix(userId);
  return key.startsWith(prefix) ? key.slice(prefix.length) : null;
}

function storageKeys(): string[] {
  const keys: string[] = [];
  try {
    for (let i = 0; i < localStorage.length; i += 1) {
      const key = localStorage.key(i);
      if (key) keys.push(key);
    }
  } catch {
    // Storage unavailable (private mode, disabled cookies) — nothing is cached.
  }
  return keys;
}

function isQuotaError(e: unknown): boolean {
  if (!(e instanceof DOMException)) return false;
  return (
    e.name === 'QuotaExceededError' ||
    e.name === 'NS_ERROR_DOM_QUOTA_REACHED' ||
    e.code === 22 ||
    e.code === 1014
  );
}

// LOCAL_STORAGE_BUDGET (M22): картинки — главный пожиратель квоты. Скриншоты агента не
// кешируются вовсе (остаётся счётчик-заглушка), у вложений остаются имя, тип и размер — пузырь
// показывает их карточкой файла. В памяти текущего визита всё остаётся как было.
function stripMessage(m: ChatMessage): ChatMessage {
  let out = m;
  if (m.screenshots && m.screenshots.length > 0) {
    out = {
      ...out,
      screenshots: [],
      screenshotsOmitted: (m.screenshotsOmitted ?? 0) + m.screenshots.length,
    };
  }
  if (m.attachments?.some((a) => a.previewUrl)) {
    out = {
      ...out,
      attachments: m.attachments.map((a) => {
        if (!a.previewUrl) return a;
        const { previewUrl: _dropped, ...rest } = a;
        return rest;
      }),
    };
  }
  return out;
}

function serializeSession(s: ChatSession): string {
  return JSON.stringify({ ...s, messages: s.messages.map(stripMessage) });
}

/** The compact form written when the cache has to make room: metadata only, messages on demand. */
function serializeEvicted(s: ChatSession): string {
  return JSON.stringify({
    ...s,
    messages: [],
    needsTranscript: true,
    // Keeps the sidebar order: without messages it sorts by createdAt.
    createdAt: lastActiveAt(s),
  });
}

/** Parses one stored chat; null for anything that is not a usable session. */
export function parseStoredSession(raw: string | null): ChatSession | null {
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as ChatSession;
    if (!parsed || typeof parsed.id !== 'string' || !Array.isArray(parsed.messages)) return null;
    return normalizeSession(parsed);
  } catch {
    return null;
  }
}

/** Every cached chat of this user, newest activity first. */
export function loadUserSessions(userId: string | null): ChatSession[] {
  if (!userId) return [];
  const prefix = userPrefix(userId);
  const sessions: ChatSession[] = [];
  for (const key of storageKeys()) {
    if (!key.startsWith(prefix)) continue;
    let raw: string | null = null;
    try {
      raw = localStorage.getItem(key);
    } catch {
      continue;
    }
    const session = parseStoredSession(raw);
    if (session && !session.incognito) sessions.push(session);
  }
  return sessions.sort((a, b) => lastActiveAt(b) - lastActiveAt(a));
}

/** Removes every cached chat of this user (logout / account switch). */
export function clearUserSessions(userId: string): void {
  const prefix = userPrefix(userId);
  for (const key of storageKeys()) {
    if (!key.startsWith(prefix)) continue;
    try {
      localStorage.removeItem(key);
    } catch {
      // ignore
    }
  }
}

// SESSION_ISOLATION (H10): старый общий массив мог принадлежать ЛЮБОМУ, кто пользовался этим
// браузером. Пользователю отдаются только те чаты оттуда, которые сервер подтвердил как его
// (`allChatIds`, контракт C-3); остальное остаётся лежать для настоящего владельца и первым
// удаляется, когда не хватает места.
/** Takes this user's chats out of the legacy array (ids confirmed by the server). */
export function takeLegacySessions(ownedIds: Set<string>): ChatSession[] {
  let raw: string | null = null;
  try {
    raw = localStorage.getItem(LEGACY_KEY);
  } catch {
    return [];
  }
  if (!raw) return [];

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    dropLegacySessions();
    return [];
  }
  if (!Array.isArray(parsed)) {
    dropLegacySessions();
    return [];
  }

  const mine: ChatSession[] = [];
  const rest: unknown[] = [];
  for (const item of parsed as ChatSession[]) {
    const id = typeof item?.id === 'string' ? item.id.toLowerCase() : null;
    if (id && ownedIds.has(id) && Array.isArray(item.messages)) mine.push(item);
    else rest.push(item);
  }
  if (mine.length === 0) return [];

  try {
    if (rest.length > 0) localStorage.setItem(LEGACY_KEY, JSON.stringify(rest));
    else localStorage.removeItem(LEGACY_KEY);
  } catch {
    // The remainder is smaller than what was there; if even that fails, drop it.
    dropLegacySessions();
  }

  return mine.map((s) => ({ ...normalizeSession(s), remote: true }));
}

function hasLegacySessions(): boolean {
  try {
    return localStorage.getItem(LEGACY_KEY) !== null;
  } catch {
    return false;
  }
}

function dropLegacySessions(): void {
  try {
    localStorage.removeItem(LEGACY_KEY);
  } catch {
    // ignore
  }
}

/**
 * Writes one user's chats to localStorage: only the chats that changed since the last write, at most
 * every {@link SAVE_INTERVAL_MS}, flushed when the page is hidden or closed.
 */
export class ChatPersistence {
  /** The exact session object last written (or loaded) per chat id — identity marks "unchanged". */
  private readonly lastWritten = new Map<string, ChatSession>();
  /** Chats currently cached in the compact (messages evicted) form. */
  private readonly evicted = new Set<string>();
  private pending: ChatSession[] | null = null;
  private timer: number | null = null;
  private disposed = false;
  private quotaWarned = false;

  constructor(
    readonly userId: string,
    /** Chats whose cached messages must never be evicted (the open chat, chats being generated). */
    private readonly isProtected: (chatId: string) => boolean,
  ) {
    window.addEventListener('pagehide', this.flush);
    window.addEventListener('beforeunload', this.flush);
    document.addEventListener('visibilitychange', this.onVisibility);
  }

  /** Marks sessions as already stored (just loaded, or received from another tab). */
  seed(sessions: ChatSession[]): void {
    for (const s of sessions) {
      if (!s.incognito) this.lastWritten.set(s.id, s);
    }
  }

  /** Records a chat another tab wrote (or removed), so this tab does not write it straight back. */
  acknowledge(chatId: string, session: ChatSession | null): void {
    this.evicted.delete(chatId);
    if (session) this.lastWritten.set(chatId, session);
    else this.lastWritten.delete(chatId);
  }

  /** Queues the current list; writes happen at most once per interval, never per token. */
  schedule(sessions: ChatSession[]): void {
    if (this.disposed) return;
    this.pending = sessions;
    if (this.timer === null) {
      this.timer = window.setTimeout(this.flush, SAVE_INTERVAL_MS);
    }
  }

  /** Writes the queued list now. */
  readonly flush = (): void => {
    if (this.timer !== null) {
      window.clearTimeout(this.timer);
      this.timer = null;
    }
    const sessions = this.pending;
    this.pending = null;
    if (!sessions || this.disposed) return;
    this.write(sessions);
  };

  /** Stops listening; `flush` writes what is still queued first. */
  dispose(flush: boolean): void {
    if (flush) this.flush();
    this.disposed = true;
    if (this.timer !== null) window.clearTimeout(this.timer);
    this.timer = null;
    this.pending = null;
    window.removeEventListener('pagehide', this.flush);
    window.removeEventListener('beforeunload', this.flush);
    document.removeEventListener('visibilitychange', this.onVisibility);
  }

  private readonly onVisibility = (): void => {
    if (document.visibilityState === 'hidden') this.flush();
  };

  private write(sessions: ChatSession[]): void {
    const present = new Set<string>();
    for (const s of sessions) {
      if (s.incognito) continue;
      present.add(s.id);
      if (this.lastWritten.get(s.id) === s) continue;
      if (this.writeOne(s, sessions)) {
        this.lastWritten.set(s.id, s);
        this.evicted.delete(s.id);
      }
    }

    // Chats that left the list in this tab (deleted, pruned) leave the cache too. Chats this tab
    // never had (created in another tab) are not in lastWritten and are left alone.
    for (const id of [...this.lastWritten.keys()]) {
      if (present.has(id)) continue;
      try {
        localStorage.removeItem(chatKey(this.userId, id));
      } catch {
        // ignore
      }
      this.lastWritten.delete(id);
      this.evicted.delete(id);
    }
  }

  private writeOne(session: ChatSession, all: ChatSession[]): boolean {
    const key = chatKey(this.userId, session.id);
    const value = serializeSession(session);
    for (;;) {
      try {
        localStorage.setItem(key, value);
        return true;
      } catch (e) {
        if (!isQuotaError(e)) {
          console.warn('[ChatStorage] could not cache the chat', { chatId: session.id, error: String(e) });
          return false;
        }
        if (!this.makeRoom(all, session.id)) {
          // Nothing left to evict. The chat stays in memory, and the next change retries — the
          // failure is logged instead of being swallowed forever.
          if (!this.quotaWarned) {
            this.quotaWarned = true;
            console.warn('[ChatStorage] localStorage is full; this chat is not cached on this device', {
              chatId: session.id,
              bytes: value.length,
            });
          }
          return false;
        }
      }
    }
  }

  /**
   * Frees space for one more write: first the legacy array, then the cached messages of the
   * oldest server-backed chats (never pinned, open or generating ones). Returns false when there
   * is nothing left to evict.
   */
  private makeRoom(all: ChatSession[], exceptId: string): boolean {
    if (hasLegacySessions()) {
      console.warn('[ChatStorage] quota reached; dropping the legacy chat cache');
      dropLegacySessions();
      return true;
    }

    const candidate = all
      .filter((s) =>
        s.id !== exceptId &&
        s.remote &&
        !s.incognito &&
        !s.isPinned &&
        !s.needsTranscript &&
        s.messages.length > 0 &&
        !this.isProtected(s.id) &&
        // Only chats already cached in full are worth rewriting in the compact form.
        this.lastWritten.get(s.id) === s &&
        !this.evicted.has(s.id),
      )
      .sort((a, b) => lastActiveAt(a) - lastActiveAt(b))[0];
    if (!candidate) return false;

    try {
      localStorage.setItem(chatKey(this.userId, candidate.id), serializeEvicted(candidate));
    } catch {
      // Could not even shrink it: drop it from the cache entirely (the server still has it).
      try {
        localStorage.removeItem(chatKey(this.userId, candidate.id));
      } catch {
        return false;
      }
    }
    console.info('[ChatStorage] quota reached; evicted cached messages of an old chat', { chatId: candidate.id });
    // The in-memory object stays the "written" one: it is rewritten in full only when it changes.
    this.evicted.add(candidate.id);
    return true;
  }
}
