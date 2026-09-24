import type { ChatMessage } from '../types/chat';

/**
 * FILE_CARDS: добавлено 2026-09-24 — какие файлы агент создал или изменил за этот ход.
 *
 * Отдельного поля на сообщении нет, но всё нужное уже приходит в него:
 *   * `logs` — строки бэкенда `[File Written] path`, `[File Patched] path`, `[Document Created] path`
 *     (инструменты file_write / file_patch / create_document в ConexyAgentRunner);
 *   * `toolActions` — события редактора `str_replace_editor` (create / str_replace / insert).
 * Из них собирается список без дублей в порядке появления.
 */

export interface ProducedFile {
  path: string;
  name: string;
  ext: string;
  /** Created during this turn (otherwise only edited). */
  created: boolean;
  /** Office documents, PDFs, tables and notes — the files a user usually wants to download. */
  document: boolean;
}

const LOG_RE = /^\[(File Written|File Patched|Document Created)\]\s+(.+?)\s*$/;

const DOCUMENT_EXTENSIONS = new Set([
  'docx', 'doc', 'odt', 'rtf', 'xlsx', 'xls', 'ods', 'csv', 'tsv', 'pptx', 'ppt', 'odp', 'pdf', 'md',
  'txt',
]);

function normalizePath(path: string): string {
  return path.trim().replace(/\\/g, '/').replace(/^\.\//, '');
}

export function fileExtension(path: string): string {
  const name = path.split('/').pop() ?? path;
  const dot = name.lastIndexOf('.');
  return dot > 0 ? name.slice(dot + 1).toLowerCase() : '';
}

export function isDocumentPath(path: string): boolean {
  return DOCUMENT_EXTENSIONS.has(fileExtension(path));
}

export function producedFiles(message: Pick<ChatMessage, 'logs' | 'toolActions'>): ProducedFile[] {
  const byPath = new Map<string, ProducedFile>();

  const add = (rawPath: string, created: boolean) => {
    const path = normalizePath(rawPath);
    if (!path || path.endsWith('/')) return;
    const existing = byPath.get(path);
    if (existing) {
      existing.created ||= created;
      return;
    }
    byPath.set(path, {
      path,
      name: path.split('/').pop() ?? path,
      ext: fileExtension(path),
      created,
      document: isDocumentPath(path),
    });
  };

  for (const line of message.logs ?? []) {
    const m = LOG_RE.exec(line);
    if (m) add(m[2], m[1] !== 'File Patched');
  }
  for (const action of message.toolActions ?? []) {
    if (action.toolName !== 'str_replace_editor' || action.status !== 'completed' || !action.path) continue;
    if (action.command === 'create') add(action.path, true);
    else if (action.command === 'str_replace' || action.command === 'insert') add(action.path, false);
  }

  return [...byPath.values()];
}
