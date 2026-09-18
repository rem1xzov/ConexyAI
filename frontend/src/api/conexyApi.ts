import http from './client';
import type {
  ConexyRequest,
  ConexyResponse,
  DevTokenResponse,
  IdeFileContent,
  SaveFileDto,
  SubscriptionUsage,
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
