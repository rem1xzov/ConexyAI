// WEB_DEVICE: добавлено 2026-10-18 — где выполняются инструменты агента: в серверной песочнице или
// на ПК пользователя (ConexyAI Desktop). Выбор и проект хранятся отдельно для каждого чата.

export type ExecutionTarget = 'sandbox' | 'device';

/** Pseudo chat id for the composer of a chat that has not been created yet. */
export const DRAFT_CHAT_KEY = 'draft';

const TARGET_PREFIX = 'conexy.deviceTarget.';
const PROJECT_PREFIX = 'conexy.deviceProject.';

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string | null) {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch {
    /* storage unavailable / full — the choice just is not remembered */
  }
}

export function readExecutionTarget(chatId: string | null | undefined): ExecutionTarget | null {
  const value = read(TARGET_PREFIX + (chatId || DRAFT_CHAT_KEY));
  return value === 'device' || value === 'sandbox' ? value : null;
}

export function writeExecutionTarget(chatId: string | null | undefined, target: ExecutionTarget) {
  write(TARGET_PREFIX + (chatId || DRAFT_CHAT_KEY), target);
}

export function readDeviceProject(chatId: string | null | undefined): string | null {
  return read(PROJECT_PREFIX + (chatId || DRAFT_CHAT_KEY));
}

export function writeDeviceProject(chatId: string | null | undefined, projectId: string | null) {
  write(PROJECT_PREFIX + (chatId || DRAFT_CHAT_KEY), projectId);
}

/**
 * The execution choice for a chat at send time. A brand-new chat inherits what was picked in the
 * draft composer, and that choice is then pinned to the chat's real id.
 */
export function resolveExecutionChoice(chatId: string): { target: ExecutionTarget; projectId: string | null } {
  const own = readExecutionTarget(chatId);
  if (own) return { target: own, projectId: readDeviceProject(chatId) };
  const draft = readExecutionTarget(DRAFT_CHAT_KEY) ?? 'sandbox';
  const draftProject = readDeviceProject(DRAFT_CHAT_KEY);
  writeExecutionTarget(chatId, draft);
  if (draftProject) writeDeviceProject(chatId, draftProject);
  return { target: draft, projectId: draftProject };
}

/** Counts added/removed lines of a unified diff (headers excluded). */
export function diffStats(diff: string): { added: number; removed: number } {
  let added = 0;
  let removed = 0;
  for (const line of diff.split('\n')) {
    if (line.startsWith('+++') || line.startsWith('---')) continue;
    if (line.startsWith('+')) added++;
    else if (line.startsWith('-')) removed++;
  }
  return { added, removed };
}

/** Heading the backend writes into the stored answer together with the ```diff block. */
export const DEVICE_DIFF_HEADING = '**Изменения на ПК:**';
