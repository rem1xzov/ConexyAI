import http from './client';

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
  /** "About me": stack, role, preferences. */
  aboutMe: string;
  /** "How ConexyAI should respond": length, language, tone, formatting. */
  responseStyle: string;
}

/** Server-side limit for each preferences field (longer values are rejected with TOO_LONG). */
export const PREFERENCE_MAX_LENGTH = 1500;

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
    aboutMe: typeof data?.aboutMe === 'string' ? data.aboutMe : '',
    responseStyle: typeof data?.responseStyle === 'string' ? data.responseStyle : '',
  };
}

/** Saves both fields; resolves with what the server stored. Rejects with 400 TOO_LONG past the limit. */
export async function savePreferences(prefs: UserPreferences): Promise<UserPreferences> {
  const { data } = await http.put<Partial<UserPreferences>>('/user/preferences', prefs);
  return {
    aboutMe: typeof data?.aboutMe === 'string' ? data.aboutMe : prefs.aboutMe,
    responseStyle: typeof data?.responseStyle === 'string' ? data.responseStyle : prefs.responseStyle,
  };
}
