import http from './client';
import axios from 'axios';
import type {
  AdminSupportTicket,
  AdminUsersResponse,
  ChatSummary,
  ChatTranscript,
  ConexyRequest,
  ConexyResponse,
  DevTokenResponse,
  IdeFileContent,
  SaveFileDto,
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

// EMAIL_AUTH: добавлено 2026-09-19
/** Registers a new email/password account and returns its session token. */
export async function register(email: string, password: string): Promise<DevTokenResponse> {
  const { data } = await axios.post<DevTokenResponse>('/api/auth/register', { email, password });
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
/** The signed-in user's chats, newest activity first (server-side list, not localStorage). */
export async function getChats(limit = 50): Promise<ChatSummary[]> {
  const { data } = await http.get<ChatSummary[]>('/conexy/chats', { params: { limit } });
  return data;
}

/** The stored transcript of one chat; the backend scopes it to the token's user. */
export async function getChatTranscript(chatId: string): Promise<ChatTranscript> {
  const { data } = await http.get<ChatTranscript>(`/conexy/chats/${chatId}/messages`);
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
