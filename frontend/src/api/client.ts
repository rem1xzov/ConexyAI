import axios, { AxiosError, AxiosInstance, InternalAxiosRequestConfig } from 'axios';

let bearerToken: string | null = null;

/** Stores the JWT used by the HTTP interceptor and the SignalR hub. */
export function setAuthToken(token: string | null): void {
  bearerToken = token;
}

export function getAuthToken(): string | null {
  return bearerToken;
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

// Transparently recover from 401: the dev JWT expires after 60 minutes (and the
// SPA otherwise never refreshes it), so on the first 401 we fetch a fresh
// development token and retry the original request exactly once.
http.interceptors.response.use(
  (response) => response,
  async (error: AxiosError) => {
    const config = error.config as (InternalAxiosRequestConfig & { _retried?: boolean }) | undefined;
    if (error.response?.status === 401 && config && !config._retried) {
      config._retried = true;
      try {
        const { data } = await axios.post<{ token: string }>('/api/auth/dev-token');
        if (data?.token) {
          setAuthToken(data.token);
          config.headers.Authorization = `Bearer ${data.token}`;
          return http(config);
        }
      } catch {
        // Keep the original 401 if the refresh itself failed.
      }
    }
    return Promise.reject(error);
  },
);

export default http;
