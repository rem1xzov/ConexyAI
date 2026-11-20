// PROJECTS: локальные «папки» чатов. Сервер про них не знает — это раскладка для навигации,
// поэтому хранится в localStorage и не влияет на сами диалоги.
const STORAGE_KEY = 'conexy_projects_v1';

export type ProjectMap = Record<string, string>;

function read(): ProjectMap {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    const parsed = raw ? JSON.parse(raw) : null;
    return parsed && typeof parsed === 'object' ? (parsed as ProjectMap) : {};
  } catch {
    return {};
  }
}

function write(map: ProjectMap) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(map));
  } catch {
    // Ignore storage restrictions.
  }
  window.dispatchEvent(new CustomEvent('conexy:projects-changed'));
}

export function getProject(sessionId: string): string {
  const map = read();
  return map[sessionId] ?? '';
}

export function setProject(sessionId: string, name: string) {
  const map = read();
  const trimmed = name.trim();
  if (trimmed) map[sessionId] = trimmed;
  else delete map[sessionId];
  write(map);
}

export function listProjects(): string[] {
  return Array.from(new Set(Object.values(read()).filter(Boolean))).sort((a, b) => a.localeCompare(b));
}

export function projectMap(): ProjectMap {
  return read();
}
