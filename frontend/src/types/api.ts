// CHAT_KIND_SYNC: добавлено 2026-09-23 — тип вкладки берётся из types/chat, чтобы не заводить
// второй источник правды (импорт type-only, поэтому цикл chat.ts ↔ api.ts безвреден).
import type { ChatSessionKind } from './chat';

// COWORK_MODE: добавлено 2026-09-23 — conexy-cowork: агент для нетехнических задач (вкладка «Агент»).
export type ConexyModel = 'ConexyV1-flash' | 'ConexyV1-pro' | 'conexy-coder' | 'conexy-cowork';

export type ReasoningEffort = 'low' | 'high' | 'max';

export interface TaskAttachment {
  fileName: string;
  contentBase64: string;
  contentType: string; // e.g. "image/png", "text/plain", "application/json"
}

// ATTACHMENT_SIZE_LIMIT: добавлено 2026-09-22
/** Result of handing a turn to the backend. `ok: false` means nothing was sent, so the composer
 *  can hand the text and the files back instead of losing them to a failed request. */
export interface SendOutcome {
  ok: boolean;
  /** The payload was rejected for its size (413 from the proxy or the server). */
  tooLarge?: boolean;
}

export interface ConexyRequest {
  model: ConexyModel;
  prompt: string;
  githubToken?: string;
  githubRepo?: string;
  attachments?: TaskAttachment[];
  thinking?: boolean;
  reasoningEffort?: ReasoningEffort;
  studentsMode?: boolean;
  sessionId?: string;
  chatId?: string;
  smartSearch?: boolean;
  // INCOGNITO_CHAT: добавлено 2026-09-20
  /** Ephemeral turn: not persisted to chat history and excluded from long-term memory. */
  incognito?: boolean;
  // CONTINUE_GENERATION: добавлено 2026-09-21
  /** Already-generated answer text; the model resumes from here instead of starting over. */
  assistantPrefix?: string;
  // CHAT_KIND_SYNC: добавлено 2026-09-23
  /** The tab this chat belongs to ('chat' | 'projects' | 'students'), persisted with the history so
   *  a chat synced to another device reopens in the same tab instead of the generic chat list. */
  chatKind?: ChatSessionKind;
}

export interface ConexyResponse {
  id: string;
  userId: string;
  model: string;
  prompt: string;
  result?: string | null;
  status: string;
  createdAt: string;
  finishedAt?: string | null;
}

// CHAT_SYNC: добавлено 2026-09-23
/** One chat in the user's list, as returned by `GET /api/conexy/chats`. */
export interface ChatSummary {
  id: string;
  /** First user message; null for a chat that has no user turn yet. */
  title?: string | null;
  /** "chat" or "projects" (the agent mode) — inferred server-side from the chat's workspace. */
  kind: string;
  lastActivityAt: string;
  messageCount: number;
  lastMessage?: string | null;
}

/** One stored turn inside a chat transcript. */
export interface ChatTranscriptMessage {
  role: string;
  content: string;
  createdAt: string;
}

/** Full stored transcript of one chat, oldest first. */
export interface ChatTranscript {
  id: string;
  messages: ChatTranscriptMessage[];
}

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
export interface SubscriptionUsage {
  tier: string;
  flashUsed: number;
  flashLimit: number;
  flashResetsAt: string;
  proUsed: number;
  proLimit: number;
  proResetsAt: string;
  agentUsed: number;
  agentLimit: number;
  agentResetsAt: string;
}

export interface LimitExceededInfo {
  limit: string;
  resetsAt: string;
}

export interface DevTokenResponse {
  token: string;
  expiresAtUtc: string;
  lifetimeMinutes: number;
}

// EMAIL_AUTH: добавлено 2026-09-19
export interface UserProfile {
  email: string;
  displayName: string;
  tier: string;
  isAdmin: boolean;
}

// ADMIN_PANEL: добавлено 2026-09-19
export interface AdminUser {
  id: string;
  email: string | null;
  gitHubUsername: string | null;
  createdAt: string;
  tier: string;
  isAdmin: boolean;
  isSuperAdmin: boolean;
}

export interface AdminUsersResponse {
  totalCount: number;
  page: number;
  pageSize: number;
  users: AdminUser[];
}

// SUPPORT: добавлено 2026-09-19
export interface SupportMessage {
  id: string;
  ticketId: string;
  senderId: string;
  content: string;
  createdAt: string;
  isFromAdmin: boolean;
}

export interface SupportTicket {
  id: string;
  userId: string;
  status: string;
  createdAt: string;
  lastMessageAt: string;
  messages: SupportMessage[];
}

export interface AdminSupportTicket {
  id: string;
  userId: string;
  userEmail: string | null;
  userGitHubUsername: string | null;
  status: string;
  createdAt: string;
  lastMessageAt: string;
  lastMessagePreview: string | null;
}

export interface WorkspaceFileEntry {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number;
  children: WorkspaceFileEntry[];
}

export interface WorkspaceListing {
  files: string[];
  tree: WorkspaceFileEntry[];
}

export interface WorkspaceFileContent {
  path: string;
  name: string;
  content: string;
}

export interface SaveFileDto {
  path: string;
  content?: string;
}

/** Content response from the IDE file API (TZ_08 /api/sessions/{id}/files/content). */
export interface IdeFileContent {
  path: string;
  content: string;
  isBinary: boolean;
}
