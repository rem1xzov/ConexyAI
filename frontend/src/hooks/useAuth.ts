import { useEffect, useRef, useState } from 'react';
import { getDevToken } from '../api/conexyApi';

const STORAGE_KEY = 'conexy_auth';

/** Refresh the token this far ahead of its expiry so it never lapses mid-session. */
const REFRESH_MARGIN_MS = 60_000;

interface StoredAuth {
  token: string;
  expiresAtUtc: string;
}

function readStoredToken(): string | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as StoredAuth;
    if (!parsed.token || !parsed.expiresAtUtc) return null;
    if (new Date(parsed.expiresAtUtc).getTime() <= Date.now()) return null;
    return parsed.token;
  } catch {
    return null;
  }
}

function readStoredExpiry(): number | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as StoredAuth;
    if (!parsed.expiresAtUtc) return null;
    const ms = new Date(parsed.expiresAtUtc).getTime();
    return Number.isFinite(ms) ? ms : null;
  } catch {
    return null;
  }
}

function storeAuth(token: string, expiresAtUtc: string): void {
  try {
    const value: StoredAuth = { token, expiresAtUtc };
    localStorage.setItem(STORAGE_KEY, JSON.stringify(value));
  } catch {
    // localStorage unavailable — ignore, token remains in memory.
  }
}

/** True when the dev-token request failed with 404 (endpoint disabled outside Development). */
function isNotFoundError(e: unknown): boolean {
  const err = e as { response?: { status?: number } } | null;
  return err?.response?.status === 404;
}

/**
 * Silent auto-auth: on mount, reuse a valid stored token (or fetch a development token in
 * the background) and keep refreshing it before it expires. No user action.
 *
 * When the dev-token endpoint returns 404 (e.g. Production, where the endpoint is disabled),
 * that is treated as a normal "not authenticated" state (`authUnavailable`) rather than an
 * error — there is no real registration/login flow yet.
 */
export function useAuth() {
  const [token, setToken] = useState<string | null>(readStoredToken);
  const [error, setError] = useState<string | null>(null);
  const [authUnavailable, setAuthUnavailable] = useState(false);
  const refreshTimerRef = useRef<number | null>(null);

  useEffect(() => {
    let cancelled = false;

    const refresh = async () => {
      try {
        const res = await getDevToken();
        if (cancelled) return;
        storeAuth(res.token, res.expiresAtUtc);
        setToken(res.token);
        setAuthUnavailable(false);
        setError(null);
        scheduleRefresh(new Date(res.expiresAtUtc).getTime());
      } catch (e) {
        if (cancelled) return;
        if (isNotFoundError(e)) {
          setAuthUnavailable(true);
          setError(null);
        } else {
          setError(e instanceof Error ? e.message : String(e));
        }
      }
    };

    const scheduleRefresh = (expiresAtMs: number) => {
      if (refreshTimerRef.current) window.clearTimeout(refreshTimerRef.current);
      const delay = expiresAtMs - Date.now() - REFRESH_MARGIN_MS;
      refreshTimerRef.current = window.setTimeout(() => void refresh(), Math.max(delay, 0));
    };

    const expiry = readStoredExpiry();
    if (expiry !== null && expiry > Date.now()) {
      // A valid token already exists; refresh it just before it expires.
      scheduleRefresh(expiry);
    } else {
      void refresh();
    }

    return () => {
      cancelled = true;
      if (refreshTimerRef.current) window.clearTimeout(refreshTimerRef.current);
    };
  }, []);

  return { token, error, authUnavailable };
}
