// MCP: добавлено 2026-10-04
/**
 * Личные MCP-серверы пользователя для агента (внешние сервисы: Notion, Jira, Linear и т.п.).
 *
 * Токен НЕ хранится на сервере в БД — он уходит вместе с конкретным запросом
 * (ConexyRequest.mcpServers → ConexyJob.McpServers, только в памяти) и живёт в браузере. Поэтому
 * агент работает под аккаунтом пользователя, а не под общим серверным.
 *
 * Хранится в localStorage. При выходе стирается (clearMcpServers в handleLogout), чтобы токены не
 * достались следующему аккаунту в этом браузере.
 */
import type { McpServerInput } from '../types/api';

const STORAGE_KEY = 'conexy_mcp_servers';

export function readMcpServers(): McpServerInput[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed
      .filter((entry): entry is McpServerInput => !!entry && typeof entry === 'object' && typeof (entry as McpServerInput).url === 'string')
      .map((entry) => ({
        id: entry.id,
        name: entry.name,
        url: entry.url.trim(),
        token: entry.token?.trim() || undefined,
        enabled: entry.enabled !== false,
      }))
      .filter((entry) => entry.url.length > 0);
  } catch {
    return [];
  }
}

export function storeMcpServers(servers: McpServerInput[]): void {
  try {
    const clean = servers
      .map((s) => ({ ...s, url: s.url.trim(), token: s.token?.trim() || undefined, name: s.name?.trim() || undefined }))
      .filter((s) => s.url.length > 0);
    if (clean.length) localStorage.setItem(STORAGE_KEY, JSON.stringify(clean));
    else localStorage.removeItem(STORAGE_KEY);
  } catch {
    /* storage may be unavailable (private mode); the agent simply has no personal MCP servers */
  }
}

export function clearMcpServers(): void {
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    /* ignore */
  }
}
