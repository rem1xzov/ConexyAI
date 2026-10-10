import http from './client';
import type { McpServerInput } from '../types/api';

// USER_MEMORY / CUSTOM_INSTRUCTIONS: добавлено 2026-09-24 — клиент для контракта C-7
// (память о пользователе и пользовательские инструкции). Ответы нормализуются: отсутствующие поля
// превращаются в пустые значения, чтобы экран настроек не падал на неполном ответе сервера.

/** One remembered fact about the user, extracted automatically from their chats. */
export interface MemoryFact {
  id: string;
  text: string;
  createdAt: string;
  updatedAt: string;
}

export interface MemoryState {
  /** When false nothing is extracted from chats and nothing is injected into prompts. */
  enabled: boolean;
  facts: MemoryFact[];
}

/** Custom instructions injected into the system prompt of every mode. */
export interface UserPreferences {
  /** USER_NAME: как обращаться к пользователю (приветствие и системный промпт). */
  name: string;
  /** "About me": stack, role, preferences. */
  aboutMe: string;
  /** "How ConexyAI should respond": length, language, tone, formatting. */
  responseStyle: string;
}

/** Server-side limit for each preferences field (longer values are rejected with TOO_LONG). */
export const PREFERENCE_MAX_LENGTH = 1500;

/** USER_NAME: имя короче свободных текстов — это просто обращение. */
export const PREFERENCE_NAME_MAX_LENGTH = 80;

function normalizeFact(raw: Partial<MemoryFact> | null | undefined): MemoryFact | null {
  if (!raw || typeof raw.id !== 'string' || !raw.id) return null;
  return {
    id: raw.id,
    text: typeof raw.text === 'string' ? raw.text : '',
    createdAt: typeof raw.createdAt === 'string' ? raw.createdAt : '',
    updatedAt: typeof raw.updatedAt === 'string' ? raw.updatedAt : (raw.createdAt ?? ''),
  };
}

export async function getMemory(): Promise<MemoryState> {
  const { data } = await http.get<Partial<MemoryState>>('/user/memory');
  const facts = Array.isArray(data?.facts)
    ? data.facts.map(normalizeFact).filter((f): f is MemoryFact => f !== null)
    : [];
  return { enabled: data?.enabled !== false, facts };
}

/** Deletes one fact; it is not re-extracted later. 404 = not this user's fact (already gone). */
export async function deleteMemoryFact(id: string): Promise<void> {
  await http.delete(`/user/memory/${encodeURIComponent(id)}`);
}

/** Deletes every remembered fact; none of them is re-extracted later. */
export async function clearMemory(): Promise<void> {
  await http.delete('/user/memory');
}

export async function setMemoryEnabled(enabled: boolean): Promise<void> {
  await http.put('/user/memory/settings', { enabled });
}

export async function getPreferences(): Promise<UserPreferences> {
  const { data } = await http.get<Partial<UserPreferences>>('/user/preferences');
  return {
    name: typeof data?.name === 'string' ? data.name : '',
    aboutMe: typeof data?.aboutMe === 'string' ? data.aboutMe : '',
    responseStyle: typeof data?.responseStyle === 'string' ? data.responseStyle : '',
  };
}

/** Saves both fields; resolves with what the server stored. Rejects with 400 TOO_LONG past the limit. */
export async function savePreferences(prefs: UserPreferences): Promise<UserPreferences> {
  const { data } = await http.put<Partial<UserPreferences>>('/user/preferences', prefs);
  return {
    name: typeof data?.name === 'string' ? data.name : prefs.name,
    aboutMe: typeof data?.aboutMe === 'string' ? data.aboutMe : prefs.aboutMe,
    responseStyle: typeof data?.responseStyle === 'string' ? data.responseStyle : prefs.responseStyle,
  };
}

// USER_INTEGRATIONS: добавлено 2026-10-10 — GitHub-токен и личные MCP-серверы хранятся на сервере
// (зашифрованными), а не только в браузере. Так они переживают смену устройства и доступны фоновым
// задачам агента. Наружу отдаётся только статус токена, не само значение.

/** Whether the server has a GitHub token stored for this user. */
export async function getGitHubTokenStatus(): Promise<boolean> {
  const { data } = await http.get<{ configured?: boolean }>('/user/github');
  return data?.configured === true;
}

/** Validates the token against GitHub and stores it; rejects with 400 TOKEN_REJECTED. */
export async function saveGitHubToken(token: string): Promise<boolean> {
  const { data } = await http.put<{ configured?: boolean }>('/user/github', { token });
  return data?.configured === true;
}

export async function clearGitHubToken(): Promise<void> {
  await http.delete('/user/github');
}

/** The user's personal MCP servers as stored on the server (tokens included, owner only). */
export async function getMcpServers(): Promise<McpServerInput[]> {
  const { data } = await http.get<{ servers?: McpServerInput[] }>('/user/mcp');
  return Array.isArray(data?.servers)
    ? data.servers.filter((s): s is McpServerInput => !!s && typeof s.url === 'string')
    : [];
}

/** Replaces the stored MCP servers; resolves with what the server actually kept. */
export async function saveMcpServers(servers: McpServerInput[]): Promise<McpServerInput[]> {
  const { data } = await http.put<{ servers?: McpServerInput[] }>('/user/mcp', { servers });
  return Array.isArray(data?.servers)
    ? data.servers.filter((s): s is McpServerInput => !!s && typeof s.url === 'string')
    : [];
}
