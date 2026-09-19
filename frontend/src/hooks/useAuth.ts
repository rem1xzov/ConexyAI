import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  getDevToken,
  getMe,
  getSession,
  login as apiLogin,
  logout as apiLogout,
  register as apiRegister,
} from '../api/conexyApi';
import type { UserProfile } from '../types/api';

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
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ token, expiresAtUtc }));
  } catch {
    // localStorage unavailable — ignore, token remains in memory.
  }
}

function clearStoredAuth(): void {
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    // ignore
  }
}

/** True when the dev-token request failed with 404 (endpoint disabled outside Development). */
function isNotFoundError(e: unknown): boolean {
  const err = e as { response?: { status?: number } } | null;
  return err?.response?.status === 404;
}

// EMAIL_AUTH: добавлено 2026-09-19
/** Extracts the backend's structured error code ({ code, message }) so the UI can translate it. */
function extractAuthErrorCode(e: unknown): string | null {
  const err = e as { response?: { data?: { code?: string } } } | null;
  return err?.response?.data?.code ?? null;
}

/** Maps a backend error code to a localized message via the i18n <c>errors.*</c> namespace. */
function authErrorMessage(e: unknown, t: (key: string) => string): string {
  const code = extractAuthErrorCode(e);
  if (code) {
    const key = `errors.${code}`;
    // i18next returns the key unchanged when a translation is missing.
    if (t(key) !== key) return t(key);
  }
  return t('errors.default');
}

/**
 * Authentication hook. On mount it first tries the httpOnly session cookie (GitHub OAuth or
 * email login) via <c>GET /api/auth/session</c>; the JWT is persisted to localStorage for the
 * <c>Authorization: Bearer</c> header and SignalR. Exposes <c>login</c>, <c>register</c> and
 * <c>logout</c> for the auth UI, plus the current <c>user</c> profile.
 */
export function useAuth() {
  const { t } = useTranslation();
  const [token, setToken] = useState<string | null>(readStoredToken);
  const [user, setUser] = useState<UserProfile | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [authUnavailable, setAuthUnavailable] = useState(false);
  // EMAIL_AUTH: добавлено 2026-09-19 — true until the initial /api/auth/session probe
  // concludes, so the UI does not flash an "not authenticated" state prematurely.
  const [initializing, setInitializing] = useState(true);
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

    // GITHUB_OAUTH: добавлено 2026-09-19 — surface a failed GitHub login (callback redirects
    // here with ?auth=error&message=...) and strip the query string from the address bar.
    const query = new URLSearchParams(window.location.search);
    if (query.get('auth') === 'error') {
      setError(t('errors.github_auth'));
      window.history.replaceState(null, '', window.location.pathname);
    }

    const init = async () => {
      let hasSession = false;

      // GITHUB_OAUTH: добавлено 2026-09-19 — prefer the httpOnly session cookie.
      try {
        const session = await getSession();
        if (cancelled) return;
        hasSession = true;
        storeAuth(session.token, session.expiresAtUtc);
        setToken(session.token);
        setAuthUnavailable(false);
        setError(null);
      } catch (e) {
        console.log('[useAuth] no session cookie (falling back):', e);
      }

      if (cancelled) return;

      if (!hasSession) {
        // Fallback: reuse a valid stored token, or fetch a dev token (development only).
        const expiry = readStoredExpiry();
        if (expiry !== null && expiry > Date.now()) {
          scheduleRefresh(expiry);
        } else {
          void refresh();
        }
      }

      setInitializing(false);
    };

    void init();

    return () => {
      cancelled = true;
      if (refreshTimerRef.current) window.clearTimeout(refreshTimerRef.current);
    };
  }, []);

  // EMAIL_AUTH: добавлено 2026-09-19 — load the user profile whenever we have a token.
  useEffect(() => {
    if (!token) {
      setUser(null);
      return;
    }

    let cancelled = false;
    getMe()
      .then((profile) => {
        if (!cancelled) setUser(profile);
      })
      .catch(() => {
        // Best-effort; the account widget simply stays hidden until a profile loads.
      });
    return () => {
      cancelled = true;
    };
  }, [token]);

  const applyToken = useCallback((res: { token: string; expiresAtUtc: string }) => {
    storeAuth(res.token, res.expiresAtUtc);
    setToken(res.token);
    setAuthUnavailable(false);
    setError(null);
  }, []);

  const login = useCallback(
    async (email: string, password: string) => {
      try {
        applyToken(await apiLogin(email, password));
      } catch (e) {
        throw new Error(authErrorMessage(e, t));
      }
    },
    [applyToken, t],
  );

  const register = useCallback(
    async (email: string, password: string) => {
      try {
        applyToken(await apiRegister(email, password));
      } catch (e) {
        throw new Error(authErrorMessage(e, t));
      }
    },
    [applyToken, t],
  );

  const logout = useCallback(async () => {
    try {
      await apiLogout();
    } catch {
      // The cookie might already be gone; still clear local state.
    }
    clearStoredAuth();
    setToken(null);
    setUser(null);
    setAuthUnavailable(false);
    setError(null);
  }, []);

  return { token, user, error, authUnavailable, initializing, login, register, logout };
}
