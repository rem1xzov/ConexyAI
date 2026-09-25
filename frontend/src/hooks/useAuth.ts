import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  getDevToken,
  getMe,
  getSession,
  login as apiLogin,
  logout as apiLogout,
  resendVerificationCode as apiResendCode,
  startRegistration as apiStartRegistration,
  verifyEmail as apiVerifyEmail,
  type VerificationChallenge,
} from '../api/conexyApi';
import { onTokenRefreshed, onUnauthorized } from '../api/client';
import type { UserProfile } from '../types/api';
import { userIdFromToken } from '../utils/authToken';

const STORAGE_KEY = 'conexy_auth';

/** Refresh the token this far ahead of its expiry so it never lapses mid-session. */
const REFRESH_MARGIN_MS = 60_000;

// DEV_TOKEN_SAME_USER: добавлено 2026-09-24 (L1) — setTimeout хранит задержку в int32: всё, что
// больше 2^31−1 мс (~24.8 суток), срабатывает НЕМЕДЛЕННО. 30-дневный токен email-входа давал
// задержку ~2.59e9 мс → мгновенное «обновление» → в Development новый случайный пользователь.
const MAX_TIMEOUT_MS = 2_147_483_647;

// PROFILE_RETRY: добавлено 2026-09-24 (L13) — /auth/me после 502/обрыва сети повторяется с
// нарастающей паузой, а не оставляет приложение без меню аккаунта до перезагрузки.
const PROFILE_RETRY_DELAYS_MS = [1_000, 2_000, 4_000, 8_000, 15_000, 30_000];

// DEV_TOKEN_SAME_USER: id dev-пользователя живёт отдельно от токена: токен может быть очищен
// (отозван, истёк), а следующий dev-токен всё равно должен выдаваться ТОМУ ЖЕ пользователю, иначе
// каждая перезагрузка в Development начиналась бы с нового пустого аккаунта.
const DEV_USER_KEY = 'conexy_dev_user';

/** 5xx, 408/429 and "no response at all" are worth retrying; 401/403/404 are answers, not glitches. */
function isRetryableProfileError(e: unknown): boolean {
  const status = statusOf(e);
  return status === undefined || status >= 500 || status === 408 || status === 429;
}

function statusOf(e: unknown): number | undefined {
  return (e as { response?: { status?: number } } | null)?.response?.status;
}

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

function readDevUserId(): string | null {
  try {
    return localStorage.getItem(DEV_USER_KEY);
  } catch {
    return null;
  }
}

function storeDevUserId(userId: string | null): void {
  try {
    if (userId) localStorage.setItem(DEV_USER_KEY, userId);
    else localStorage.removeItem(DEV_USER_KEY);
  } catch {
    // ignore
  }
}

/** True when the dev-token request failed with 404 (endpoint disabled outside Development). */
function isNotFoundError(e: unknown): boolean {
  return statusOf(e) === 404;
}

// EMAIL_AUTH: добавлено 2026-09-19
/** Extracts the backend's structured error code ({ code, message }) so the UI can translate it. */
function extractAuthErrorCode(e: unknown): string | null {
  const err = e as { response?: { data?: { code?: string } } } | null;
  return err?.response?.data?.code ?? null;
}

// AUTH_RATE_LIMIT: добавлено 2026-09-24 (M20) — 429 TOO_MANY_ATTEMPTS несёт паузу в теле
// (`retryAfterSeconds`) и в заголовке Retry-After.
function retryAfterSeconds(e: unknown): number | null {
  const response = (e as {
    response?: { data?: { retryAfterSeconds?: unknown }; headers?: Record<string, unknown> };
  } | null)?.response;
  const fromBody = Number(response?.data?.retryAfterSeconds);
  if (Number.isFinite(fromBody) && fromBody > 0) return Math.ceil(fromBody);
  const fromHeader = Number(response?.headers?.['retry-after']);
  return Number.isFinite(fromHeader) && fromHeader > 0 ? Math.ceil(fromHeader) : null;
}

type Translate = (key: string, options?: Record<string, unknown>) => string;

// EMAIL_VERIFICATION: добавлено 2026-09-24 — сколько попыток ввести код осталось (сервер считает их
// сам, форма только показывает).
function attemptsLeft(e: unknown): number | null {
  const value = Number((e as { response?: { data?: { attemptsLeft?: unknown } } } | null)?.response?.data?.attemptsLeft);
  return Number.isFinite(value) && value > 0 ? Math.floor(value) : null;
}

/** Maps a backend error code to a localized message via the i18n <c>errors.*</c> namespace. */
function authErrorMessage(e: unknown, t: Translate): string {
  const code = extractAuthErrorCode(e) ?? (statusOf(e) === 429 ? 'too_many_attempts' : null);
  if (code === 'too_many_attempts') {
    const seconds = retryAfterSeconds(e);
    return seconds
      ? `${t('errors.too_many_attempts')} ${t('sync.retryAfter', { seconds })}`
      : t('errors.too_many_attempts');
  }
  if (code === 'invalid_code') {
    const left = attemptsLeft(e);
    return left ? t('errors.invalid_code_left', { count: left }) : t('errors.invalid_code');
  }
  if (code === 'resend_cooldown') {
    const seconds = retryAfterSeconds(e);
    return seconds ? t('errors.resend_cooldown', { seconds }) : t('errors.resend_cooldown_short');
  }
  if (code) {
    const key = `errors.${code}`;
    // i18next returns the key unchanged when a translation is missing.
    if (t(key) !== key) return t(key);
  }
  return t('errors.default');
}

// GITHUB_OAUTH: изменено 2026-09-24 — коды, с которыми колбэк GitHub возвращает на `/?auth=error`:
// `github_auth_failed` и `too_many_attempts`.
function githubErrorMessage(code: string | null, t: Translate): string {
  if (code === 'too_many_attempts') return t('errors.too_many_attempts');
  return t('errors.github_auth');
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
  // PROFILE_RETRY: /auth/me окончательно не удался (после всех повторов) — UI показывает ошибку
  // с кнопкой «Повторить» вместо вечной «Загрузки…».
  const [profileFailed, setProfileFailed] = useState(false);
  const [profileAttempt, setProfileAttempt] = useState(0);
  const refreshTimerRef = useRef<number | null>(null);
  // DEV_TOKEN_SAME_USER: актуальный токен для таймера обновления (эффект ниже живёт весь срок хука).
  const tokenRef = useRef(token);
  tokenRef.current = token;
  const expiredTimerRef = useRef<number | null>(null);
  const tRef = useRef(t);
  tRef.current = t;

  // SESSION_REVOKED: сообщение «сессия закончилась» гаснет само — от пользователя нужен только вход.
  const showSessionExpired = useCallback(() => {
    const message = tRef.current('sync.sessionExpired');
    setError(message);
    if (expiredTimerRef.current) window.clearTimeout(expiredTimerRef.current);
    expiredTimerRef.current = window.setTimeout(() => {
      setError((current) => (current === message ? null : current));
    }, 10_000);
  }, []);

  useEffect(() => {
    let cancelled = false;

    /** Fetches a development token for the same user; false when there is none to be had. */
    const refresh = async (): Promise<boolean> => {
      try {
        // DEV_TOKEN_SAME_USER: обновляем токен ТОГО ЖЕ пользователя. Без userId dev-эндпоинт
        // создаёт нового случайного пользователя — вместе с пустым списком чатов.
        const currentUserId = userIdFromToken(tokenRef.current ?? readStoredToken()) ?? readDevUserId();
        const res = await getDevToken(currentUserId ?? undefined);
        if (cancelled) return false;
        storeAuth(res.token, res.expiresAtUtc);
        storeDevUserId(userIdFromToken(res.token));
        setToken(res.token);
        setAuthUnavailable(false);
        setError(null);
        scheduleRefresh(new Date(res.expiresAtUtc).getTime());
        return true;
      } catch (e) {
        if (cancelled) return false;
        if (isNotFoundError(e)) {
          setAuthUnavailable(true);
        } else {
          console.warn('[useAuth] dev token unavailable', { error: String(e) });
        }
        return false;
      }
    };

    const scheduleRefresh = (expiresAtMs: number) => {
      if (refreshTimerRef.current) window.clearTimeout(refreshTimerRef.current);
      const delay = expiresAtMs - Date.now() - REFRESH_MARGIN_MS;
      // DEV_TOKEN_SAME_USER: длинную задержку проходим «ступеньками» не длиннее int32 и на
      // каждой ступеньке пересчитываем остаток, а не обновляем токен раньше срока.
      if (delay > MAX_TIMEOUT_MS) {
        refreshTimerRef.current = window.setTimeout(() => scheduleRefresh(expiresAtMs), MAX_TIMEOUT_MS);
        return;
      }
      refreshTimerRef.current = window.setTimeout(() => void refresh(), Math.max(delay, 0));
    };

    // DEV_TOKEN_SAME_USER: токен, обновлённый HTTP-клиентом по 401, сохраняется и здесь — иначе
    // следующий рендер вернул бы в клиент старый, уже отвергнутый токен.
    const unsubscribeRefresh = onTokenRefreshed((res) => {
      if (cancelled) return;
      storeAuth(res.token, res.expiresAtUtc);
      setToken(res.token);
      const expires = new Date(res.expiresAtUtc).getTime();
      if (Number.isFinite(expires)) scheduleRefresh(expires);
    });

    // SESSION_REVOKED: добавлено 2026-09-24 (M19) — сервер отверг текущий токен (отозван выходом
    // на другом устройстве или админом, устарел без версии, пользователь удалён). Одна точка
    // очистки для REST и хаба: приложение переходит в состояние «не вошёл», без циклов повторов.
    const unsubscribeUnauthorized = onUnauthorized(() => {
      if (cancelled) return;
      if (refreshTimerRef.current) window.clearTimeout(refreshTimerRef.current);
      clearStoredAuth();
      tokenRef.current = null;
      setToken(null);
      setUser(null);
      showSessionExpired();
    });

    // GITHUB_OAUTH: добавлено 2026-09-19 — surface a failed GitHub login (callback redirects
    // here with ?auth=error&message=...) and strip the query string from the address bar.
    const query = new URLSearchParams(window.location.search);
    if (query.get('auth') === 'error') {
      setError(githubErrorMessage(query.get('message'), tRef.current));
      window.history.replaceState(null, '', window.location.pathname + window.location.hash);
    }

    const init = async () => {
      const storedToken = readStoredToken();

      // GITHUB_OAUTH: добавлено 2026-09-19 — prefer the httpOnly session cookie.
      try {
        const session = await getSession();
        if (cancelled) return;
        storeAuth(session.token, session.expiresAtUtc);
        setToken(session.token);
        setAuthUnavailable(false);
        setError(null);
        setInitializing(false);
        return;
      } catch (e) {
        if (cancelled) return;
        if (statusOf(e) === 401) {
          // SESSION_REVOKED: сервер сказал «сессии нет» (нет cookie, истекла или отозвана). В
          // Development — dev-токен ТОГО ЖЕ пользователя; иначе сохранённый токен больше не
          // используется: он так же недействителен, и с ним приложение зависало без меню аккаунта.
          const refreshed = await refresh();
          if (cancelled) return;
          if (!refreshed) {
            clearStoredAuth();
            tokenRef.current = null;
            setToken(null);
            // Existing users meet this once after the deploy that made tokens revocable.
            if (storedToken) showSessionExpired();
          }
          setInitializing(false);
          return;
        }
        console.log('[useAuth] session probe failed (falling back):', e);
      }

      // Offline / 5xx: keep a stored token until the API itself says otherwise (onUnauthorized).
      const expiry = readStoredExpiry();
      if (expiry !== null && expiry > Date.now()) {
        scheduleRefresh(expiry);
      } else {
        void refresh();
      }
      setInitializing(false);
    };

    void init();

    return () => {
      cancelled = true;
      unsubscribeRefresh();
      unsubscribeUnauthorized();
      if (refreshTimerRef.current) window.clearTimeout(refreshTimerRef.current);
      if (expiredTimerRef.current) window.clearTimeout(expiredTimerRef.current);
    };
  }, [showSessionExpired]);

  // SESSION_ISOLATION: добавлено 2026-09-24 (H10) — профиль прошлого аккаунта не должен
  // показываться, пока грузится профиль нового (обновление токена того же пользователя его не трогает).
  const profileUserId = userIdFromToken(token);
  useEffect(() => {
    setUser(null);
  }, [profileUserId]);

  // EMAIL_AUTH: добавлено 2026-09-19 — load the user profile whenever we have a token.
  // PROFILE_RETRY: изменено 2026-09-24 (L13) — с повторами после 5xx/сетевых ошибок.
  useEffect(() => {
    if (!token) {
      setUser(null);
      setProfileFailed(false);
      return;
    }

    let cancelled = false;
    let timer: number | null = null;
    setProfileFailed(false);

    const attempt = (n: number) => {
      getMe()
        .then((profile) => {
          if (cancelled) return;
          setUser(profile);
          setProfileFailed(false);
        })
        .catch((e: unknown) => {
          if (cancelled) return;
          if (isRetryableProfileError(e) && n < PROFILE_RETRY_DELAYS_MS.length) {
            console.warn('[useAuth] /auth/me failed; retrying', { attempt: n + 1, error: String(e) });
            timer = window.setTimeout(() => attempt(n + 1), PROFILE_RETRY_DELAYS_MS[n]);
            return;
          }
          console.warn('[useAuth] /auth/me failed for good', { error: String(e) });
          setProfileFailed(true);
        });
    };
    attempt(0);

    return () => {
      cancelled = true;
      if (timer !== null) window.clearTimeout(timer);
    };
  }, [token, profileAttempt]);

  /** Loads the profile again after it failed for good (the "Retry" button). */
  const reloadProfile = useCallback(() => setProfileAttempt((n) => n + 1), []);

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

  // EMAIL_VERIFICATION: изменено 2026-09-24 — регистрация возвращает задачу подтверждения, а не
  // сессию: пароль уходит на сервер, там он ждёт кода, и только confirmEmail выдаёт токен.
  const register = useCallback(
    async (email: string, password: string): Promise<VerificationChallenge> => {
      try {
        return await apiStartRegistration(email, password);
      } catch (e) {
        throw new Error(authErrorMessage(e, t));
      }
    },
    [t],
  );

  // EMAIL_VERIFICATION: ввод кода из письма — здесь появляется сессия.
  const confirmEmail = useCallback(
    async (email: string, code: string) => {
      try {
        applyToken(await apiVerifyEmail(email, code));
      } catch (e) {
        throw new Error(authErrorMessage(e, t));
      }
    },
    [applyToken, t],
  );

  // EMAIL_VERIFICATION: повторная отправка; сервер отвечает своим кулдауном, чтобы форма показала
  // отсчёт, даже если она его потеряла.
  const resendCode = useCallback(
    async (email: string): Promise<number> => {
      try {
        const res = await apiResendCode(email);
        return res.resendCooldownSeconds;
      } catch (e) {
        throw new Error(authErrorMessage(e, t));
      }
    },
    [t],
  );

  // SESSION_REVOKED: изменено 2026-09-24 (M19) — выход отзывает токен на сервере (Bearer + cookie),
  // а локальное состояние аккаунта (чаты, хаб, ходы) App чистит по смене пользователя (H10).
  const logout = useCallback(async () => {
    try {
      await apiLogout(tokenRef.current);
    } catch {
      // The cookie might already be gone; still clear local state.
    }
    if (refreshTimerRef.current) window.clearTimeout(refreshTimerRef.current);
    clearStoredAuth();
    storeDevUserId(null);
    tokenRef.current = null;
    setToken(null);
    setUser(null);
    setAuthUnavailable(false);
    setError(null);
  }, []);

  return {
    token,
    user,
    error,
    authUnavailable,
    initializing,
    profileFailed,
    reloadProfile,
    login,
    register,
    confirmEmail,
    resendCode,
    logout,
  };
}
