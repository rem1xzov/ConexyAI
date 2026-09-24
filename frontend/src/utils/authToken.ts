// SESSION_ISOLATION: добавлено 2026-09-24 (H10, L1)
// Id пользователя нужен клиенту синхронно, раньше чем придёт /auth/me: по нему разделяется
// localStorage разных аккаунтов и отличается «обновили токен того же пользователя» от «вошёл
// другой пользователь». Подпись здесь НЕ проверяется — это не авторизация, а ключ для кеша;
// доступ к данным всё равно решает сервер по самому токену.

const NAME_IDENTIFIER_CLAIM = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier';

function decodeBase64Url(segment: string): string {
  const base64 = segment.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4);
  const binary = atob(padded);
  // The payload is UTF-8 JSON; atob yields Latin-1 code units.
  const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}

/**
 * The user id (`sub`, or the .NET name-identifier claim) carried by a JWT, lowercased; null when the
 * token is missing or not a readable JWT.
 */
export function userIdFromToken(token: string | null | undefined): string | null {
  if (!token) return null;
  const parts = token.split('.');
  if (parts.length < 2) return null;
  try {
    const payload = JSON.parse(decodeBase64Url(parts[1])) as Record<string, unknown>;
    const id = payload.sub ?? payload[NAME_IDENTIFIER_CLAIM] ?? payload.nameid;
    return typeof id === 'string' && id.trim() ? id.trim().toLowerCase() : null;
  } catch {
    return null;
  }
}
