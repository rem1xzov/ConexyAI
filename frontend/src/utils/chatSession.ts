import type { ChatSummary, ChatTranscript, ConexyModel } from '../types/api';
import type { ChatMessage, ChatSession, ChatSessionKind } from '../types/chat';

// CHAT_SESSIONS_MODULE: добавлено 2026-09-24 — чистые функции над сессиями чата, вынесенные из
// App.tsx: их используют и App, и слой хранения (utils/chatStorage.ts), и синхронизация.

export function uid(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }

  // CHAT_SYNC: fallback обязателен именно в форме UUID. Идентификатор чата уезжает на сервер как
  // `chatId`, и история диалога пишется под ним же; если он не парсится как Guid, бэкенд молча
  // подменяет chatId на taskId — и такой чат потом невозможно ни синхронизировать, ни найти.
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === 'x' ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

// COWORK_MODE: добавлено 2026-09-23 — оба режима агента живут во вкладке «Агент».
export function isAgentModel(model: ConexyModel): boolean {
  return model === 'conexy-coder' || model === 'conexy-cowork';
}

export function normalizeMessage(m: ChatMessage): ChatMessage {
  return {
    ...m,
    thinking: m.thinking ?? '',
    logs: m.logs ?? [],
    screenshots: m.screenshots ?? [],
    toolActions: m.toolActions ?? [],
    todos: m.todos ?? [],
    problems: m.problems ?? [],
  };
}

function normalizeModel(raw: unknown): ConexyModel {
  return raw === 'conexy-coder' || raw === 'Conexy-coder' ? 'conexy-coder'
    : raw === 'conexy-cowork' ? 'conexy-cowork'
    : raw === 'ConexyV1-pro' ? 'ConexyV1-pro'
    : 'ConexyV1-flash';
}

export function normalizeSession(s: ChatSession): ChatSession {
  const model = normalizeModel(s.model);
  const messages = (s.messages ?? []).map(normalizeMessage);
  const last = messages[messages.length - 1];
  const streaming = last?.role === 'assistant' && last.status === 'streaming';
  return {
    ...s,
    model,
    kind: s.kind ?? (isAgentModel(model) ? 'projects' : 'chat'),
    messages,
    // TURN_SCOPE: «Running» без идущего ответа — след оборванной вкладки. Он держал кнопку «Стоп»
    // и статус-бар в состоянии «работает» навсегда.
    status: s.status === 'Running' && !streaming ? 'Idle' : s.status,
  };
}

/** Last activity used for ordering (same rule as the sidebar). */
export function lastActiveAt(s: ChatSession): number {
  return s.messages.length > 0 ? s.messages[s.messages.length - 1].createdAt : s.createdAt;
}

/** True while the chat's last answer is still being generated (in this tab or, via storage, another). */
export function isSessionStreaming(s: ChatSession | null | undefined): boolean {
  if (!s) return false;
  const last = s.messages[s.messages.length - 1];
  return Boolean(last && last.role === 'assistant' && last.status === 'streaming');
}

// CHAT_KIND_SYNC: добавлено 2026-09-23
/**
 * The tab a server chat belongs to. The mode is persisted with the history, so a chat synced to a
 * second device reopens in the same tab — including the students tab, which cannot be inferred from
 * anything else. Unknown or missing values fall back to the generic chat tab.
 */
export function kindFromServer(kind: string | null | undefined): ChatSessionKind {
  return kind === 'projects' || kind === 'students' ? kind : 'chat';
}

// CHAT_MODEL_SYNC: добавлено 2026-09-24 (M18) — модель берётся из ChatSummaryDto.model (C-3), и
// Cowork-чат на другом устройстве / по ссылке открывается как Cowork, а не как Coder.
/** The model a server chat should reopen with, constrained to what its tab can run. */
export function modelFromServer(kind: ChatSessionKind, model: string | null | undefined): ConexyModel {
  if (kind === 'projects') return model === 'conexy-cowork' ? 'conexy-cowork' : 'conexy-coder';
  // Students always runs Pro.
  if (kind === 'students') return 'ConexyV1-pro';
  return model === 'ConexyV1-pro' ? 'ConexyV1-pro' : 'ConexyV1-flash';
}

/** Stored transcript rows as transcript messages (tool/system rows are internal plumbing). */
export function messagesFromTranscript(transcript: ChatTranscript | null | undefined): ChatMessage[] {
  return (transcript?.messages ?? [])
    .filter((m) => m.role === 'user' || m.role === 'assistant')
    .map((m) => normalizeMessage({
      id: uid(),
      role: m.role as 'user' | 'assistant',
      content: m.content,
      status: 'complete',
      createdAt: new Date(m.createdAt).getTime(),
    }));
}

// CHAT_SYNC: добавлено 2026-09-23
/**
 * Builds a local session from a server chat list entry plus its stored transcript. Used for chats
 * this device has never seen (created on another device), which is what makes the sidebar no longer
 * device-local. Without a transcript the session is created as a stub that loads it on open.
 */
export function sessionFromServer(
  chat: ChatSummary,
  transcript: ChatTranscript | null,
  fallbackTitle: string,
): ChatSession {
  const kind = kindFromServer(chat.kind);
  const messages = messagesFromTranscript(transcript);
  const lastActivity = new Date(chat.lastActivityAt).getTime();

  return {
    id: chat.id,
    title: chat.title || fallbackTitle,
    status: 'Completed',
    // CHAT_DELETE: чат пришёл с сервера, поэтому его можно и удалять локально, если сервер
    // перестанет его отдавать (см. prune в syncChats).
    remote: true,
    // CHAT_MODEL_SYNC: последняя модель чата с сервера (C-3), с учётом вкладки.
    model: modelFromServer(kind, chat.model),
    kind,
    messages,
    // CHAT_PIN: закрепление, сделанное на другом устройстве, приезжает вместе с чатом.
    isPinned: chat.isPinned,
    // A stub without messages is ordered by its last activity (the sidebar falls back to createdAt).
    createdAt: messages.length > 0 ? messages[0].createdAt : (Number.isFinite(lastActivity) ? lastActivity : Date.now()),
    needsTranscript: transcript ? undefined : true,
    serverActivityAt: chat.lastActivityAt,
    serverMessageCount: chat.messageCount,
    unsyncedTurns: 0,
  };
}

function flatten(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

function sameMessage(local: ChatMessage, server: ChatMessage): boolean {
  if (local.role !== server.role) return false;
  const l = flatten(local.content);
  const s = flatten(server.content);
  if (l === s) return true;
  // The server stores the user text together with the extracted text of attached documents, and a
  // stopped answer locally is a prefix of what the server kept.
  return l.length > 0 && s.startsWith(l);
}

// TRANSCRIPT_REFRESH: добавлено 2026-09-24 (M25)
/**
 * Brings a local transcript up to date with the server's one. The common prefix keeps the LOCAL
 * messages (they carry agent steps, attachments, todo lists the server does not store); everything
 * after the first difference is taken from the server, which is the source of truth for a chat that
 * was continued elsewhere. Returns null when the server has nothing the local copy lacks.
 */
export function mergeTranscript(local: ChatMessage[], server: ChatMessage[]): ChatMessage[] | null {
  let i = 0;
  while (i < local.length && i < server.length && sameMessage(local[i], server[i])) i += 1;
  if (i >= server.length) return null;
  return [...local.slice(0, i), ...server.slice(i)];
}

/** Runs `fn` over `items` with at most `limit` calls in flight; results keep the input order. */
export async function mapLimit<T, R>(items: T[], limit: number, fn: (item: T) => Promise<R>): Promise<R[]> {
  const results = new Array<R>(items.length);
  let next = 0;
  const worker = async () => {
    while (next < items.length) {
      const index = next;
      next += 1;
      results[index] = await fn(items[index]);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
  return results;
}
