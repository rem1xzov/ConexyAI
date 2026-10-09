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

// MCP: добавлено 2026-10-04 — личный MCP-сервер пользователя (URL + токен). Токен живёт только
// в браузере и в памяти запроса; агент вызывает внешние сервисы под аккаунтом пользователя.
export interface McpServerInput {
  id?: string;
  name?: string;
  url: string;
  token?: string;
  enabled?: boolean;
}

// ATTACHMENT_SIZE_LIMIT: добавлено 2026-09-22
/** Result of handing a turn to the backend. `ok: false` means nothing was sent, so the composer
 *  can hand the text and the files back instead of losing them to a failed request. */
export interface SendOutcome {
  ok: boolean;
  /** The payload was rejected for its size (413 from the proxy or the server). */
  tooLarge?: boolean;
  // TURN_GUARD: добавлено 2026-09-24 (H6, контракт C-2)
  /** Why nothing was sent: a turn of this chat is still running (`busy`, also 409 TURN_IN_FLIGHT)
   *  or the chat belongs to another account (`forbidden`, 403 CHAT_FORBIDDEN). */
  reason?: 'busy' | 'forbidden' | 'loading';
}

// LEGAL_DOCS: добавлено 2026-09-26
/**
 * Operator details for the public legal pages, served by `GET /api/legal/operator`. They live in the
 * server environment (`Operator__*`) rather than in the frontend bundle, so the data can be changed
 * without rebuilding the site.
 */
export interface OperatorProfile {
  name: string;
  inn: string;
  address: string;
  email: string;
  phone: string;
  /** Дата публикации оферты; у остальных документов пусто. */
  publishedAt: string;
  updatedAt: string;
  transferCountries: string;
  site: string;
}

export interface ConexyRequest {
  model: ConexyModel;
  prompt: string;
  githubToken?: string;
  githubRepo?: string;
  // MCP: личные MCP-серверы (URL + токен) — только для агентских режимов.
  mcpServers?: McpServerInput[];
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
  // ORCHESTRA: добавлено 2026-09-28 — пожелание включить «Оркестр агентов». Решение принимает
  // сервер: доступно только на ProMax и только режиму Coder, в остальных случаях флаг снимается.
  orchestra?: boolean;
  // REGENERATE_REPLACES_TURN: добавлено 2026-09-24 (контракт C-10, review M5)
  /** The turn replaces the chat's LAST stored turn (regenerate, resend or edit of the last user
   *  message) instead of appending a duplicate user row. Never set for an ordinary new message. */
  regenerate?: boolean;
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
  lastMessagePreview?: string | null;
  // CHAT_PIN: добавлено 2026-09-23 — закреплён ли чат наверху сайдбара (общий флаг для всех устройств).
  isPinned: boolean;
  // CHAT_MODEL_SYNC: добавлено 2026-09-24 (M18, контракт C-3) — последняя модель чата:
  // "ConexyV1-flash" | "ConexyV1-pro" | "conexy-coder" | "conexy-cowork"; null у старых строк.
  model?: string | null;
}

// CHAT_LIST_COMPLETE: добавлено 2026-09-24 (H8, контракт C-3)
/** `GET /api/conexy/chats`: the newest chats (plus every pinned one) and the COMPLETE id set.
 *  `allChatIds` is null only when talking to an older backend that returned a bare array — then the
 *  client must not prune anything, because the list may be truncated. */
export interface ChatListResponse {
  chats: ChatSummary[];
  allChatIds: string[] | null;
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

// SHARE_PUBLIC: добавлено 2026-10-01
/** Публичная ссылка на чат: токен и hash-путь. */
export interface ChatShareLink {
  token: string;
  path: string;
}

/** Публично расшаренный чат (только чтение), доступный без входа в аккаунт. */
export interface SharedChat {
  id: string;
  title?: string | null;
  kind: string;
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
  // COWORK_BUDGET: добавлено 2026-09-26 — у Cowork свой пул токенов. `coworkLimit === 0` означает,
  // что режим не входит в тариф (на Free), и строку о нём показывать не нужно.
  coworkUsed: number;
  coworkLimit: number;
  coworkResetsAt: string;
  // CACHE_STATS: сырой объём входных токенов, прочитанных из кэша (для показа «со скидкой»), и
  // процент этой скидки. На лимит не влияет — его считает взвешенная сумма выше.
  agentCachedTokens: number;
  coworkCachedTokens: number;
  cacheHitDiscountPercent: number;
}

export interface LimitExceededInfo {
  limit: string;
  resetsAt: string;
}

// YOOKASSA: добавлено 2026-09-27 — платежи за тарифы.
/** Ответ на создание платежа: куда редиректить пользователя. */
export interface CreatePaymentResponse {
  paymentId: string;
  status: string;
  confirmationUrl: string;
  amountRub: number;
  planId: string;
}

/** Статус платежа для окна возврата: `pending` | `succeeded` | `canceled` и т.п. */
export interface PaymentStatusResponse {
  paymentId: string;
  status: string;
  planId: string;
  tier: string;
  amountRub: number;
  // TOKEN_TOPUP: добавлено 2026-10-06 — 'subscription' | 'token'. У докупки токенов tier пустой.
  kind: string;
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

// YOOKASSA: добавлено 2026-09-27 — сводка оплат для ручных чеков в «Мой налог».
/** Одна строка сводки: дата оплаты, сумма, тариф и покупатель. */
export interface AdminPaymentSummaryItem {
  paymentId: string;
  paidAt: string;
  amountRub: number;
  planId: string;
  tier: string;
  userId: string;
  email: string | null;
  username: string | null;
}

/** Успешные платежи за период и их общая сумма (`from`/`to` — московские даты). */
export interface AdminPaymentSummary {
  from: string;
  to: string;
  totalRub: number;
  count: number;
  payments: AdminPaymentSummaryItem[];
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

// IDE_GIT: добавлено 2026-10-04 — Source Control (панель git в IDE).
export interface GitFileChange {
  path: string;
  indexStatus: string;
  workTreeStatus: string;
  staged: boolean;
  /** Single-letter badge the backend computes for the row. */
  status: string;
}

export interface GitStatusResult {
  success: boolean;
  isRepository: boolean;
  branch?: string | null;
  changes: GitFileChange[];
  error?: string | null;
}

export interface GitCommitInfo {
  hash: string;
  shortHash: string;
  author: string;
  date: string;
  subject: string;
}

export interface GitLogResult {
  success: boolean;
  commits: GitCommitInfo[];
  error?: string | null;
}

export interface GitBranchInfo {
  name: string;
  isCurrent: boolean;
}

export interface GitBranchesResult {
  success: boolean;
  branches: GitBranchInfo[];
  error?: string | null;
}

export interface GitOperationResult {
  success: boolean;
  message?: string | null;
  error?: string | null;
}

// IDE_DIFF: добавлено 2026-10-05 — diff-редактор «до/после» одного файла.
export interface GitFileDiffResult {
  success: boolean;
  path: string;
  revision: string;
  hasOriginal: boolean;
  hasModified: boolean;
  binary: boolean;
  original: string;
  modified: string;
  error?: string | null;
}

// SEARCH_REPLACE: добавлено 2026-10-05 — глобальный поиск и замена по проекту.
export interface SearchMatch {
  path: string;
  line: number;
  column: number;
  length: number;
  preview: string;
}

export interface ProjectSearchResult {
  success: boolean;
  matches: SearchMatch[];
  totalMatches: number;
  filesSearched: number;
  truncated: boolean;
  error?: string | null;
}

export interface ReplaceFileResult {
  path: string;
  count: number;
}

export interface ProjectReplaceResult {
  success: boolean;
  filesChanged: number;
  replacements: number;
  files: ReplaceFileResult[];
  error?: string | null;
}

// PROBLEMS_PANEL: добавлено 2026-10-04 — единая панель проблем (ошибки/предупреждения всего проекта).
export interface ProblemItem {
  file: string;
  line: number;
  column: number;
  /** "error" | "warning" | "info" */
  severity: string;
  code: string;
  message: string;
}

export interface ProblemsResult {
  success: boolean;
  tool?: string | null;
  errors: number;
  warnings: number;
  problems: ProblemItem[];
  error?: string | null;
}

// LSP_LITE: добавлено 2026-10-05 — Outline, go-to-definition, hover и подсказки по символам проекта.
export interface IdeSymbolNode {
  name: string;
  kind: string;
  detail?: string | null;
  line: number;
  column: number;
  endLine: number;
  endColumn: number;
  children: IdeSymbolNode[];
}

export interface IdeSymbolsResult {
  success: boolean;
  path?: string | null;
  language?: string | null;
  symbols: IdeSymbolNode[];
  error?: string | null;
}

export interface IdeLocation {
  path: string;
  line: number;
  column: number;
  kind: string;
  text: string;
}

export interface IdeDefinitionResult {
  found: boolean;
  word: string;
  locations: IdeLocation[];
  error?: string | null;
}

export interface IdeHoverResult {
  found: boolean;
  word: string;
  kind?: string | null;
  signature?: string | null;
  documentation?: string | null;
  path?: string | null;
  line: number;
  error?: string | null;
}

export interface IdeSymbolSearchItem {
  name: string;
  kind: string;
  path: string;
  line: number;
  column: number;
}

export interface IdeSymbolSearchResult {
  success: boolean;
  symbols: IdeSymbolSearchItem[];
  error?: string | null;
}

// DEBUG_TRACE: добавлено 2026-10-05 — точки останова и трассировка выполнения Python (уровень A).
export interface DebugBreakpoint {
  path: string;
  line: number;
}

export interface DebugBreakpointsResult {
  success: boolean;
  breakpoints: DebugBreakpoint[];
  error?: string | null;
}

export interface DebugStep {
  file: string;
  line: number;
  function: string;
  locals: Record<string, string>;
  stack: string[];
  breakpoint: boolean;
}

export interface DebugResult {
  success: boolean;
  language?: string | null;
  steps: DebugStep[];
  output: string;
  programError?: string | null;
  error?: string | null;
  truncated: boolean;
  exitCode: number;
}
