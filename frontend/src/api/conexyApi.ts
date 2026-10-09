import http from './client';
import axios from 'axios';
import type {
  AdminPaymentSummary,
  AdminSupportTicket,
  AdminUsersResponse,
  ChatListResponse,
  ChatShareLink,
  ChatSummary,
  ChatTranscript,
  ConexyRequest,
  ConexyResponse,
  DebugBreakpoint,
  DebugBreakpointsResult,
  DebugResult,
  DevTokenResponse,
  GitBranchesResult,
  GitFileDiffResult,
  GitLogResult,
  GitOperationResult,
  GitStatusResult,
  IdeDefinitionResult,
  IdeFileContent,
  IdeHoverResult,
  IdeSymbolSearchResult,
  IdeSymbolsResult,
  ProblemsResult,
  ProjectReplaceResult,
  ProjectSearchResult,
  SaveFileDto,
  SharedChat,
  SubscriptionUsage,
  SupportMessage,
  SupportTicket,
  UserProfile,
  WorkspaceFileContent,
  WorkspaceListing,
} from '../types/api';

/** Issues a development JWT. The backend only exposes this endpoint in Development. */
export async function getDevToken(userId?: string): Promise<DevTokenResponse> {
  const { data } = await http.post<DevTokenResponse>('/auth/dev-token', null, {
    params: userId ? { userId } : undefined,
  });
  return data;
}

// GITHUB_OAUTH: добавлено 2026-09-19
/**
 * Returns the JWT stored in the httpOnly session cookie (set by the GitHub OAuth callback).
 * Uses a plain axios call (not the `http` instance) so the 401-retry interceptor does not
 * fire — a missing session is a normal "not authenticated" state, not a token-expiry event.
 */
export async function getSession(): Promise<DevTokenResponse> {
  const { data } = await axios.get<DevTokenResponse>('/api/auth/session');
  return data;
}

// EMAIL_VERIFICATION: изменено 2026-09-24 — регистрация двухшаговая. Сервер запоминает выбранный
// пароль, шлёт на адрес 6-значный код и НЕ выдаёт сессию: токен появляется только после confirmEmail.
/** What `/api/auth/register` answers: the code was sent, there is no session yet. */
export interface VerificationChallenge {
  email: string;
  resendCooldownSeconds: number;
}

/** Starts a sign-up: validates the address and password, mails a code. No token yet. */
export async function startRegistration(
  email: string,
  password: string,
  acceptedPolicy: boolean,
): Promise<VerificationChallenge> {
  // PRIVACY_POLICY: добавлено 2026-09-25 — сервер отказывает в регистрации без явного согласия,
  // поэтому флаг обязателен в теле запроса, а не только в состоянии формы.
  const { data } = await axios.post<VerificationChallenge>('/api/auth/register', {
    email,
    password,
    acceptedPolicy,
  });
  return data;
}

/** Confirms the emailed code — the account is created here and the session token comes back. */
export async function verifyEmail(email: string, code: string): Promise<DevTokenResponse> {
  const { data } = await axios.post<DevTokenResponse>('/api/auth/verify-email', { email, code });
  return data;
}

/** Asks for a fresh code for a pending sign-up; the server replies with the cooldown it enforces. */
export async function resendVerificationCode(email: string): Promise<{ resendCooldownSeconds: number }> {
  const { data } = await axios.post<{ resendCooldownSeconds: number }>('/api/auth/resend-code', { email });
  return data;
}

// PASSWORD_RESET: добавлено 2026-09-26
/**
 * Starts a password reset for an existing account. The server checks the address up front (it answers
 * `email_not_found` for a stranger) and mails a 6-digit code; the new password is chosen in
 * {@link resetPassword} together with that code.
 */
export async function forgotPassword(email: string): Promise<VerificationChallenge> {
  const { data } = await axios.post<VerificationChallenge>('/api/auth/forgot-password', { email });
  return data;
}

// PASSWORD_RESET: добавлено 2026-09-26
/** Confirms the reset code and applies the new password; the session token comes back with it. */
export async function resetPassword(
  email: string,
  code: string,
  newPassword: string,
): Promise<DevTokenResponse> {
  const { data } = await axios.post<DevTokenResponse>('/api/auth/reset-password', {
    email,
    code,
    newPassword,
  });
  return data;
}

// EMAIL_AUTH: добавлено 2026-09-19
/** Logs in with an email/password account and returns its session token. */
export async function login(email: string, password: string): Promise<DevTokenResponse> {
  const { data } = await axios.post<DevTokenResponse>('/api/auth/login', { email, password });
  return data;
}

// EMAIL_AUTH: добавлено 2026-09-19
// SESSION_REVOKED: изменено 2026-09-24 (M19) — выход отзывает токен на сервере (все устройства),
// поэтому сам токен передаётся явно, а не только через cookie.
/** Revokes the session on the backend and clears its cookie (logout). */
export async function logout(token?: string | null): Promise<void> {
  await axios.post('/api/auth/logout', null, {
    headers: token ? { Authorization: `Bearer ${token}` } : undefined,
  });
}

// EMAIL_AUTH: добавлено 2026-09-19
/** Returns the authenticated user's profile (name, tier, admin flag). */
export async function getMe(): Promise<UserProfile> {
  const { data } = await http.get<UserProfile>('/auth/me');
  return data;
}

// ADMIN_PANEL: добавлено 2026-09-19
export async function getAdminUsers(page = 1, pageSize = 20): Promise<AdminUsersResponse> {
  const { data } = await http.get<AdminUsersResponse>('/admin/users', {
    params: { page, pageSize },
  });
  return data;
}

export async function makeAdmin(id: string): Promise<void> {
  await http.post(`/admin/users/${id}/make-admin`);
}

export async function revokeAdmin(id: string): Promise<void> {
  await http.post(`/admin/users/${id}/revoke-admin`);
}

export async function deleteUser(id: string): Promise<void> {
  await http.delete(`/admin/users/${id}`);
}

// YOOKASSA: добавлено 2026-09-27 — сводка успешных оплат за период (для ручных чеков).
/** `from`/`to` — московские даты в формате YYYY-MM-DD (обе включительно). */
export async function getAdminPaymentsSummary(from: string, to: string): Promise<AdminPaymentSummary> {
  const { data } = await http.get<AdminPaymentSummary>('/admin/payments/summary', {
    params: { from, to },
  });
  return data;
}

// SUPPORT: добавлено 2026-09-19
export async function createSupportTicket(): Promise<SupportTicket> {
  const { data } = await http.post<SupportTicket>('/support/tickets');
  return data;
}

export async function getMySupportTicket(): Promise<SupportTicket | null> {
  try {
    const { data } = await http.get<SupportTicket>('/support/tickets/mine');
    return data;
  } catch (e) {
    if ((e as { response?: { status?: number } }).response?.status === 404) return null;
    throw e;
  }
}

export async function sendSupportMessage(ticketId: string, content: string): Promise<SupportMessage> {
  const { data } = await http.post<SupportMessage>(`/support/tickets/${ticketId}/messages`, { content });
  return data;
}

// SUPPORT_BOT: переходы состояния поддержки. Каждый возвращает обновлённый тикет.
export async function escalateSupportTicket(ticketId: string): Promise<SupportTicket> {
  const { data } = await http.post<SupportTicket>(`/support/tickets/${ticketId}/escalate`);
  return data;
}

export async function returnSupportTicketToBot(ticketId: string): Promise<SupportTicket> {
  const { data } = await http.post<SupportTicket>(`/support/tickets/${ticketId}/return-to-bot`);
  return data;
}

export async function cancelSupportTicket(ticketId: string): Promise<SupportTicket> {
  const { data } = await http.post<SupportTicket>(`/support/tickets/${ticketId}/cancel`);
  return data;
}

export async function getAdminSupportTickets(status?: string, search?: string): Promise<AdminSupportTicket[]> {
  const { data } = await http.get<AdminSupportTicket[]>('/admin/support/tickets', {
    params: { status, search },
  });
  return data;
}

export async function getAdminSupportTicket(id: string): Promise<SupportTicket> {
  const { data } = await http.get<SupportTicket>(`/admin/support/tickets/${id}`);
  return data;
}

export async function closeSupportTicket(id: string): Promise<void> {
  await http.post(`/admin/support/tickets/${id}/close`);
}

/** Submits a task; the backend returns 202 Accepted with the created entity. */
export async function runTask(request: ConexyRequest): Promise<ConexyResponse> {
  const { data } = await http.post<ConexyResponse>('/conexy/run', request);
  return data;
}

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
/** Returns the current user's tier and per-limit usage (for the donut indicator). */
export async function getSubscriptionUsage(): Promise<SubscriptionUsage> {
  const { data } = await http.get<SubscriptionUsage>('/subscription/usage');
  return data;
}

/** Sends recorded audio (raw 16 kHz LPCM) to SpeechKit STT and returns the recognized text. */
// LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
// export async function recognizeSpeech(audio: Blob): Promise<string> {
//   if (!audio || audio.size === 0) {
//     throw new Error('Empty audio recording');
//   }
//   const { data } = await http.post<{ text: string }>('/speech/recognize', audio, {
//     headers: { 'Content-Type': 'audio/x-pcm' },
//   });
//   return data.text ?? '';
// }

/** Synthesizes Russian speech for the given text; returns a WAV blob. */
// LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17 (озвучка убрана из UI)
// export async function synthesizeSpeech(text: string): Promise<Blob> {
//   const { data } = await http.post<Blob>('/speech/synthesize', { text }, { responseType: 'blob' });
//   return data;
// }

export async function getTaskStatus(id: string): Promise<ConexyResponse> {
  const { data } = await http.get<ConexyResponse>(`/conexy/${id}`);
  return data;
}

// CHAT_SYNC: добавлено 2026-09-23
// CHAT_LIST_COMPLETE: изменено 2026-09-24 (H8, контракт C-3) — ответ теперь
// `{ chats, allChatIds }`: последние чаты + все закреплённые и ПОЛНЫЙ набор id. Раньше приходили
// только 50 последних, и синк удалял всё, что не попало в список, — в том числе закреплённые чаты.
/** The signed-in user's chats, newest activity first (server-side list, not localStorage). */
export async function getChats(limit = 200): Promise<ChatListResponse> {
  const { data } = await http.get<ChatListResponse | ChatSummary[]>('/conexy/chats', { params: { limit } });
  // Старый бэкенд отдаёт голый массив: он может быть обрезан, поэтому полного набора id нет.
  if (Array.isArray(data)) return { chats: data, allChatIds: null };
  return {
    chats: Array.isArray(data?.chats) ? data.chats : [],
    allChatIds: Array.isArray(data?.allChatIds) ? data.allChatIds : null,
  };
}

// CHAT_SHARE_LINK: добавлено 2026-09-24 — чат по ссылке может быть старше первых N из списка.
/** One chat of the signed-in user as the list shows it; null when it is not theirs (404). */
export async function getChat(chatId: string): Promise<ChatSummary | null> {
  try {
    const { data } = await http.get<ChatSummary>(`/conexy/chats/${chatId}`);
    return data;
  } catch (err: unknown) {
    const status = (err as { response?: { status?: number } })?.response?.status;
    if (status === 404 || status === 403) return null;
    throw err;
  }
}

/** The stored transcript of one chat; the backend scopes it to the token's user. */
export async function getChatTranscript(chatId: string): Promise<ChatTranscript> {
  const { data } = await http.get<ChatTranscript>(`/conexy/chats/${chatId}/messages`);
  return data;
}

// SHARE_PUBLIC: добавлено 2026-10-01
/** Создаёт (или обновляет) публичную ссылку на чат. Только владелец. */
export async function createChatShareLink(chatId: string): Promise<ChatShareLink> {
  const { data } = await http.post<ChatShareLink>(`/conexy/chats/${chatId}/share`);
  return data;
}

/** Отзывает публичную ссылку на чат. */
export async function revokeChatShareLink(chatId: string): Promise<void> {
  await http.delete(`/conexy/chats/${chatId}/share`);
}

/**
 * Читает публично расшаренный чат по токену. Эндпоинт анонимный, поэтому идём голым axios: сессия
 * тут не нужна, а её отсутствие не должно превращаться в «выход из аккаунта» или повторные запросы.
 */
export async function getSharedChat(token: string): Promise<SharedChat> {
  const { data } = await axios.get<SharedChat>(`/api/conexy/shared/${encodeURIComponent(token)}`);
  return data;
}

// CHAT_DELETE: добавлено 2026-09-23
/**
 * Permanently deletes a chat on the server (history rows + its workspace). 404 means the chat does
 * not belong to this user — the caller must keep it locally rather than pretending it was deleted.
 */
export async function deleteChat(chatId: string): Promise<void> {
  await http.delete(`/conexy/chats/${chatId}`);
}

// CHAT_RENAME: добавлено 2026-09-23
/**
 * Renames a chat on the server, so the new name reaches every device.
 * 404 means the chat is not this user's (or was never persisted) — the caller keeps its local name.
 */
export async function renameChat(chatId: string, title: string): Promise<void> {
  await http.patch(`/conexy/chats/${chatId}`, { title });
}

// CHAT_PIN: добавлено 2026-09-23
/**
 * Pins or unpins a chat on the server, so the sidebar order reaches every device.
 * 404 means the chat is not this user's (or was never persisted) — the caller keeps its local flag.
 */
export async function setChatPinned(chatId: string, isPinned: boolean): Promise<void> {
  await http.patch(`/conexy/chats/${chatId}/pin`, { isPinned });
}

export async function getWorkspaceFiles(sessionId: string): Promise<WorkspaceListing> {
  const { data } = await http.get<WorkspaceListing>(`/conexy/workspace/${sessionId}/files`);
  return data;
}

export async function getWorkspaceFile(sessionId: string, path: string): Promise<WorkspaceFileContent> {
  const { data } = await http.get<WorkspaceFileContent>(`/conexy/workspace/${sessionId}/file`, {
    params: { path },
  });
  return data;
}

export async function downloadWorkspaceZip(sessionId: string): Promise<Blob> {
  const { data } = await http.get<Blob>(`/conexy/workspace/${sessionId}/download-zip`, {
    responseType: 'blob',
  });
  return data;
}

// OFFICE_FORMATS: добавлено 2026-09-23
export type OfficeFormat = 'docx' | 'xlsx' | 'pptx';

/** Any model's answer as a Word / Excel / PowerPoint file, rendered server-side from its Markdown. */
export async function exportDocument(format: OfficeFormat, markdown: string, fileName?: string): Promise<Blob> {
  const { data } = await http.post<Blob>('/conexy/export', { format, markdown, fileName }, { responseType: 'blob' });
  return data;
}

/** A single workspace file as-is — binary documents cannot go through the text endpoint. */
export async function downloadWorkspaceRaw(sessionId: string, path: string): Promise<Blob> {
  const { data } = await http.get<Blob>(`/conexy/workspace/${sessionId}/raw`, {
    params: { path },
    responseType: 'blob',
  });
  return data;
}

/** Uploads a ZIP archive that gets extracted into the session workspace. */
export async function uploadWorkspaceZip(sessionId: string, file: File): Promise<void> {
  const formData = new FormData();
  formData.append('file', file);
  await http.post(`/conexy/workspace/${sessionId}/upload-zip`, formData, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
}

export function downloadWorkspaceFile(sessionId: string, path: string): Promise<WorkspaceFileContent> {
  return getWorkspaceFile(sessionId, path);
}

export async function saveWorkspaceFile(sessionId: string, dto: SaveFileDto): Promise<void> {
  await http.put(`/conexy/workspace/${sessionId}/file`, dto);
}

export async function deleteWorkspaceFile(sessionId: string, path: string): Promise<void> {
  await http.delete(`/conexy/workspace/${sessionId}/file`, {
    params: { path },
  });
}

/** Reads a workspace file through the IDE file API (returns isBinary flag). */
export async function getIdeFileContent(sessionId: string, path: string): Promise<IdeFileContent> {
  const { data } = await http.get<IdeFileContent>(`/sessions/${sessionId}/files/content`, {
    params: { path },
  });
  return data;
}

/** Saves manual user edits through the IDE file API (invalidates the agent's undo stack). */
export async function saveIdeFileContent(sessionId: string, path: string, content: string): Promise<void> {
  await http.put(`/sessions/${sessionId}/files/content`, { path, content });
}

/** Creates a new file (or directory) in the workspace via the IDE file API. */
export async function createIdeFile(sessionId: string, path: string, isDirectory = false): Promise<void> {
  await http.post(`/sessions/${sessionId}/files`, { path, isDirectory });
}

// IDE_DELETE: добавлено 2026-09-24 — удаление файла или папки через IDE API (для WorkspacePanel).
/** Deletes a file or a folder in the workspace via the IDE file API. */
export async function deleteIdePath(sessionId: string, path: string): Promise<void> {
  await http.delete(`/sessions/${sessionId}/files`, { params: { path } });
}

// FILE_TREE_UX: добавлено 2026-10-05 — переименование и перемещение (drag&drop) в дереве файлов.
/** Renames or moves a file/folder in the workspace. */
export async function renameIdeFile(sessionId: string, oldPath: string, newPath: string): Promise<void> {
  await http.post(`/sessions/${sessionId}/files/rename`, { oldPath, newPath });
}

// IDE_GIT: добавлено 2026-10-04 — Source Control через IDE API.
/** Working-tree status of the workspace repository. */
export async function getGitStatus(sessionId: string, repoFolder?: string): Promise<GitStatusResult> {
  const { data } = await http.get<GitStatusResult>(`/sessions/${sessionId}/git/status`, { params: { repoFolder } });
  return data;
}

/** Recent commits of the current branch. */
export async function getGitLog(sessionId: string, limit = 30, repoFolder?: string): Promise<GitLogResult> {
  const { data } = await http.get<GitLogResult>(`/sessions/${sessionId}/git/log`, { params: { limit, repoFolder } });
  return data;
}

/** Local branches of the workspace repository. */
export async function getGitBranches(sessionId: string, repoFolder?: string): Promise<GitBranchesResult> {
  const { data } = await http.get<GitBranchesResult>(`/sessions/${sessionId}/git/branches`, { params: { repoFolder } });
  return data;
}

// IDE_DIFF: добавлено 2026-10-05 — содержимое файла в ревизии (по умолчанию HEAD) против рабочей копии.
/** Returns a file's content at a revision versus its working-tree content, for the diff view. */
export async function getGitFileDiff(
  sessionId: string,
  path: string,
  rev?: string,
  repoFolder?: string,
): Promise<GitFileDiffResult> {
  const { data } = await http.get<GitFileDiffResult>(`/sessions/${sessionId}/git/file`, { params: { path, rev, repoFolder } });
  return data;
}

// SEARCH_REPLACE: добавлено 2026-10-05 — глобальный поиск и замена по проекту.
/** Searches the workspace for a string or regular expression. */
export async function searchProject(
  sessionId: string,
  query: string,
  regex: boolean,
  caseSensitive: boolean,
  includeGlob?: string,
): Promise<ProjectSearchResult> {
  const { data } = await http.post<ProjectSearchResult>(`/sessions/${sessionId}/search`, {
    query,
    regex,
    caseSensitive,
    includeGlob,
  });
  return data;
}

/** Replaces a string or regular expression across the workspace (or a single file). */
export async function replaceProject(
  sessionId: string,
  query: string,
  replacement: string,
  regex: boolean,
  caseSensitive: boolean,
  includeGlob?: string,
  path?: string,
): Promise<ProjectReplaceResult> {
  const { data } = await http.post<ProjectReplaceResult>(`/sessions/${sessionId}/search/replace`, {
    query,
    replacement,
    regex,
    caseSensitive,
    includeGlob,
    path,
  });
  return data;
}

/** Stages or unstages paths (empty list = all). */
export async function stageGitPaths(
  sessionId: string,
  paths: string[],
  staged: boolean,
  repoFolder?: string,
): Promise<GitOperationResult> {
  const { data } = await http.post<GitOperationResult>(`/sessions/${sessionId}/git/stage`, { paths, staged, repoFolder });
  return data;
}

/** Commits the staged changes locally (no push). */
export async function commitGit(
  sessionId: string,
  message: string,
  authorName?: string,
  authorEmail?: string,
  repoFolder?: string,
): Promise<GitOperationResult> {
  const { data } = await http.post<GitOperationResult>(`/sessions/${sessionId}/git/commit`, {
    message,
    authorName,
    authorEmail,
    repoFolder,
  });
  return data;
}

/** Switches to an existing branch. */
export async function checkoutGitBranch(
  sessionId: string,
  branch: string,
  repoFolder?: string,
): Promise<GitOperationResult> {
  const { data } = await http.post<GitOperationResult>(`/sessions/${sessionId}/git/checkout`, { branch, repoFolder });
  return data;
}

// PROBLEMS_PANEL: добавлено 2026-10-04 — анализ ошибок/предупреждений всего проекта.
/** Analyzes the workspace (compiler/linter/types) and returns structured problems. */
export async function analyzeProblems(
  sessionId: string,
  tool?: string,
  path?: string,
): Promise<ProblemsResult> {
  const { data } = await http.post<ProblemsResult>(`/sessions/${sessionId}/problems/analyze`, { tool, path });
  return data;
}

// LSP_LITE: добавлено 2026-10-05 — Outline, go-to-definition, hover и подсказки по символам.
// Текст передаётся из живого буфера редактора, поэтому несохранённые правки тоже видны.
/** Symbol tree (outline) of a file. */
export async function getIdeSymbols(
  sessionId: string,
  path: string,
  content?: string,
): Promise<IdeSymbolsResult> {
  const { data } = await http.post<IdeSymbolsResult>(`/sessions/${sessionId}/symbols`, { path, content });
  return data;
}

/** Declarations of an identifier across the workspace. */
export async function findIdeDefinition(
  sessionId: string,
  path: string,
  word: string,
  content?: string,
): Promise<IdeDefinitionResult> {
  const { data } = await http.post<IdeDefinitionResult>(`/sessions/${sessionId}/definition`, { path, word, content });
  return data;
}

/** Signature and doc comment for the identifier under the cursor. */
export async function getIdeHover(
  sessionId: string,
  path: string,
  word: string,
  content?: string,
): Promise<IdeHoverResult> {
  const { data } = await http.post<IdeHoverResult>(`/sessions/${sessionId}/hover`, { path, word, content });
  return data;
}

/** Project-wide symbol lookup by name prefix (used for completion). */
export async function searchIdeSymbols(
  sessionId: string,
  query: string,
  limit = 30,
): Promise<IdeSymbolSearchResult> {
  const { data } = await http.post<IdeSymbolSearchResult>(`/sessions/${sessionId}/symbols/search`, { query, limit });
  return data;
}

// DEBUG_TRACE: добавлено 2026-10-05 — точки останова и трассировка выполнения Python.
/** Breakpoints saved for the chat. */
export async function getDebugBreakpoints(sessionId: string): Promise<DebugBreakpointsResult> {
  const { data } = await http.get<DebugBreakpointsResult>(`/sessions/${sessionId}/debug/breakpoints`);
  return data;
}

/** Replaces the chat's saved breakpoints. */
export async function setDebugBreakpoints(
  sessionId: string,
  breakpoints: DebugBreakpoint[],
): Promise<DebugBreakpointsResult> {
  const { data } = await http.put<DebugBreakpointsResult>(`/sessions/${sessionId}/debug/breakpoints`, { breakpoints });
  return data;
}

/** Runs the entry script under the trace harness and returns the recorded steps. */
export async function runDebug(
  sessionId: string,
  path: string,
  breakpoints: DebugBreakpoint[],
  recordAll: boolean,
): Promise<DebugResult> {
  const { data } = await http.post<DebugResult>(`/sessions/${sessionId}/debug/run`, { path, breakpoints, recordAll });
  return data;
}
