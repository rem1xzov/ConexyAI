// GITHUB_PAT_PER_USER: добавлено 2026-09-30
/**
 * Личный GitHub-токен пользователя для агента (clone/create_branch/commit_and_push/PR).
 *
 * Замысел контракта: токен НЕ хранится на сервере в БД — он уходит вместе с конкретным запросом
 * (ConexyRequest.githubToken → ConexyJob.githubToken, только в памяти) и живёт в браузере. Поэтому
 * это единственное место, где он лежит; на сервере в это поле ничего не подставляется, и коммиты
 * уходят от узкого аккаунта-владельца, а не от общего серверного PAT.
 *
 * Хранится в localStorage: как и сессионный JWT, это данные текущего пользователя в этом браузере.
 * При выходе токен стирается (см. clearGitHubToken в handleLogout), чтобы не утечь следующему аккаунту.
 */
const STORAGE_KEY = 'conexy_github_token';

export function readGitHubToken(): string | null {
  try {
    const value = localStorage.getItem(STORAGE_KEY);
    return value && value.trim() ? value.trim() : null;
  } catch {
    return null;
  }
}

export function storeGitHubToken(token: string): void {
  try {
    const value = token.trim();
    if (value) localStorage.setItem(STORAGE_KEY, value);
    else localStorage.removeItem(STORAGE_KEY);
  } catch {
    /* storage may be unavailable (private mode); the agent simply has no token this session */
  }
}

export function clearGitHubToken(): void {
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    /* ignore */
  }
}
