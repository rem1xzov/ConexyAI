import axios, { AxiosError, AxiosInstance, InternalAxiosRequestConfig } from 'axios';
import { userIdFromToken } from '../utils/authToken';

let bearerToken: string | null = null;

/** Stores the JWT used by the HTTP interceptor and the SignalR hub. */
export function setAuthToken(token: string | null): void {
  bearerToken = token;
}

export function getAuthToken(): string | null {
  return bearerToken;
}

// DEV_TOKEN_SAME_USER: добавлено 2026-09-24 (L1)
/** A token obtained by the 401 recovery below, so the auth hook can persist and adopt it. */
export interface RefreshedToken {
  token: string;
  expiresAtUtc: string;
}

const refreshListeners = new Set<(token: RefreshedToken) => void>();

/** Subscribes to tokens refreshed by the HTTP client; returns an unsubscribe function. */
export function onTokenRefreshed(listener: (token: RefreshedToken) => void): () => void {
  refreshListeners.add(listener);
  return () => {
    refreshListeners.delete(listener);
  };
}

// SESSION_REVOKED: добавлено 2026-09-24 (M19/H10) — сервер теперь отзывает токены (выход на всех
// устройствах, действие админа, удалённый пользователь, токен без версии). Любой 401 на запрос,
// сделанный с ТЕКУЩИМ токеном, — от REST или от negotiate/reconnect хаба — сходится в ОДНУ точку
// очистки. Без повторов по кругу: один токен сообщается один раз.
const unauthorizedListeners = new Set<() => void>();
let reportedToken: string | null = null;

/** Subscribes to "the current session is no longer valid"; returns an unsubscribe function. */
export function onUnauthorized(listener: () => void): () => void {
  unauthorizedListeners.add(listener);
  return () => {
    unauthorizedListeners.delete(listener);
  };
}

function reportUnauthorized(token: string, source: string): void {
  // A request made with an older token (a login/refresh happened meanwhile) says nothing about the
  // current session; and each token is reported once.
  if (token !== bearerToken || reportedToken === token) return;
  reportedToken = token;
  console.warn('[auth] the session is no longer valid; signing out', { source });
  unauthorizedListeners.forEach((listener) => listener());
}

// Параллельные 401 не должны выпрашивать по токену каждый.
let refreshInFlight: Promise<RefreshedToken | null> | null = null;
// Tokens minted by the recovery below: if one of them is refused too, recovering again would loop.
const recoveredTokens = new Set<string>();

/**
 * Re-issues a development token for the SAME user as the expired one. Without a known user id there
 * is nothing to refresh: asking the dev endpoint without `userId` mints a brand-new random user,
 * which is how a single expired request used to silently swap the whole account (and its chats).
 * Outside Development the endpoint answers 404 and nothing is refreshed.
 */
function refreshDevToken(expiredToken: string): Promise<RefreshedToken | null> {
  const userId = userIdFromToken(expiredToken);
  if (!userId || recoveredTokens.has(expiredToken)) return Promise.resolve(null);

  if (!refreshInFlight) {
    refreshInFlight = axios
      .post<RefreshedToken>('/api/auth/dev-token', null, { params: { userId } })
      .then(({ data }) => {
        if (!data?.token) return null;
        // A token for somebody else must never be adopted, whatever the endpoint returned.
        if (userIdFromToken(data.token) !== userId) return null;
        recoveredTokens.add(data.token);
        setAuthToken(data.token);
        refreshListeners.forEach((listener) => listener(data));
        return data;
      })
      .catch(() => null)
      .finally(() => {
        refreshInFlight = null;
      });
  }
  return refreshInFlight;
}

/**
 * Handles a 401 for a request (or hub connection) made with `sentToken`: in Development one silent
 * refresh for the same user, otherwise the single sign-out path. Returns the new token, or null
 * when the session is over.
 */
export async function recoverFromUnauthorized(sentToken: string | null, source: string): Promise<string | null> {
  // Already signed out: a straggling request must not sign anybody back in.
  if (!sentToken || !bearerToken) return null;
  // Superseded meanwhile (login, refresh): the current token is still worth a try.
  if (sentToken !== bearerToken) return bearerToken;
  const refreshed = await refreshDevToken(sentToken);
  if (refreshed) return refreshed.token;
  reportUnauthorized(sentToken, source);
  return null;
}

function bearerOf(config: InternalAxiosRequestConfig): string | null {
  const header = config.headers?.Authorization;
  if (typeof header !== 'string' || !header.startsWith('Bearer ')) return null;
  return header.slice('Bearer '.length);
}

const http: AxiosInstance = axios.create({
  baseURL: '/api',
  headers: {
    'Content-Type': 'application/json',
  },
});

http.interceptors.request.use((config: InternalAxiosRequestConfig) => {
  if (bearerToken) {
    config.headers.Authorization = `Bearer ${bearerToken}`;
  }
  return config;
});

// Transparently recover from 401: the dev JWT expires after 60 minutes, so on the first 401 a fresh
// development token FOR THE SAME USER is fetched and the request retried exactly once. When that is
// not possible (production, revoked token) the session is ended through onUnauthorized — once.
http.interceptors.response.use(
  (response) => response,
  async (error: AxiosError) => {
    const config = error.config as (InternalAxiosRequestConfig & { _retried?: boolean }) | undefined;
    if (error.response?.status === 401 && config) {
      const sentToken = bearerOf(config);
      if (!config._retried) {
        config._retried = true;
        const next = await recoverFromUnauthorized(sentToken, 'api');
        if (next) {
          config.headers.Authorization = `Bearer ${next}`;
          return http(config);
        }
      } else if (sentToken) {
        // Even the fresh token was refused: the session is over.
        reportUnauthorized(sentToken, 'api-retry');
      }
    }
    return Promise.reject(error);
  },
);

export default http;
