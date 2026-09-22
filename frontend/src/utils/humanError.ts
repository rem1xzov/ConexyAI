type Translate = (key: string, options?: Record<string, unknown>) => string;

// DEPLOY_WINDOW_GRACEFUL_ERRORS: добавлено 2026-09-23
/**
 * Turns anything that was thrown into a short, human, safe message.
 *
 * Why this exists: during a deploy the origin is briefly gone and the edge (Cloudflare) answers with
 * a full HTML error page — "Bad gateway / Host: conexai.ru / Error". Any code path that surfaces a
 * raw response body would have pasted that markup straight into the UI. This helper guarantees that
 * cannot happen: markup is detected and dropped, long/multi-line bodies are flattened and truncated,
 * and known infrastructure statuses get a calm, actionable sentence instead of a status code.
 *
 * `t` is passed in rather than imported so the helper stays usable from anywhere and localised.
 */
export function humanError(err: unknown, t: Translate): string {
  const e = (err ?? {}) as {
    response?: { status?: number; data?: unknown };
    message?: unknown;
    code?: unknown;
  };

  const status = e.response?.status;
  const fromBody = extractServerMessage(e.response?.data);

  // A gateway/proxy status means the backend was unreachable, not that the request was wrong.
  if (status === 502 || status === 503 || status === 504) {
    return t('errors.serviceUnavailable');
  }

  // Connection-level failure: no HTTP response at all.
  const code = typeof e.code === 'string' ? e.code : '';
  const rawMessage = typeof e.message === 'string' ? e.message : '';
  if (code === 'ERR_NETWORK' || rawMessage === 'Network Error') {
    return t('errors.network');
  }

  if (status) {
    return fromBody ?? `${t('errors.httpStatus')} ${status}`;
  }

  return fromBody ?? safeText(rawMessage) ?? t('errors.default');
}

/** Reads `{ error | message | detail }` off a JSON error body; ignores anything that is markup. */
function extractServerMessage(data: unknown): string | null {
  if (typeof data === 'string') return safeText(data);

  if (data && typeof data === 'object') {
    const record = data as Record<string, unknown>;
    for (const key of ['error', 'message', 'detail']) {
      const value = record[key];
      if (typeof value === 'string') {
        const text = safeText(value);
        if (text) return text;
      }
    }
  }

  return null;
}

const HTML_LIKE = /<!doctype|<html|<head|<body|<\/\w+\s*>/i;
const MAX_LENGTH = 220;

/**
 * Flattens and truncates a candidate message, or returns null when it looks like an HTML error page.
 * Returning null lets the caller fall back to a localised sentence instead of showing markup.
 */
function safeText(value: unknown): string | null {
  if (typeof value !== 'string') return null;

  const text = value.trim();
  if (!text) return null;
  if (HTML_LIKE.test(text)) return null;
  // A very long body with stray angle brackets is markup too, just without a doctype.
  if (text.length > 600 && text.includes('<')) return null;

  const flat = text.replace(/\s+/g, ' ').trim();
  return flat.length > MAX_LENGTH ? `${flat.slice(0, MAX_LENGTH)}…` : flat;
}
