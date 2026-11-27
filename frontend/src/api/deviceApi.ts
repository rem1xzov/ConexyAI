import http from './client';

// DESKTOP_AGENT: добавлено 2026-10-15 — ключи десктопного приложения (ConexyAI Desktop).

export interface DeviceKey {
  id: string;
  name: string;
  /** Первые символы ключа — только чтобы опознать его в списке. */
  keyPrefix: string;
  createdAt: string;
  lastUsedAt: string | null;
  revoked: boolean;
}

export interface IssuedDeviceKey {
  /** Полный ключ. Сервер показывает его ровно один раз и хранит только хэш. */
  key: string;
  info: DeviceKey;
}

// TELEGRAM_LOGIN: вход в личный Telegram идёт на ПК пользователя (там работает Telegram MCP); сайт лишь управляет им.
export interface TelegramLoginStatus {
  state: 'idle' | 'starting' | 'waiting' | 'password' | 'done' | 'failed';
  /** tg://login?token=… — открыть в Telegram на телефоне или отсканировать как QR. */
  url?: string | null;
  user?: string | null;
  error?: string | null;
  badPassword?: boolean;
}

export async function startTelegramLogin(apiId: string, apiHash: string): Promise<TelegramLoginStatus> {
  const { data } = await http.post<TelegramLoginStatus>('/device/telegram/start', { apiId, apiHash }, { timeout: 130_000 });
  return data;
}

export async function getTelegramLoginStatus(): Promise<TelegramLoginStatus> {
  const { data } = await http.get<TelegramLoginStatus>('/device/telegram/status', { timeout: 130_000 });
  return data;
}

export async function sendTelegramPassword(password: string): Promise<TelegramLoginStatus> {
  const { data } = await http.post<TelegramLoginStatus>('/device/telegram/password', { password });
  return data;
}

export async function cancelTelegramLogin(): Promise<void> {
  await http.post('/device/telegram/cancel', {});
}

export interface DesktopClientInfo {
  downloadUrl: string;
  platform: string;
  /** Актуальная версия клиента (последний релиз), null — неизвестна. */
  version: string | null;
}

export async function getDesktopClientInfo(): Promise<DesktopClientInfo> {
  const { data } = await http.get<DesktopClientInfo>('/device/client');
  return data;
}

export async function listDeviceKeys(): Promise<DeviceKey[]> {
  const { data } = await http.get<DeviceKey[]>('/device/keys');
  return Array.isArray(data) ? data : [];
}

export async function issueDeviceKey(name: string): Promise<IssuedDeviceKey> {
  const { data } = await http.post<IssuedDeviceKey>('/device/keys', { name });
  return data;
}

export async function revokeDeviceKey(id: string): Promise<void> {
  await http.delete(`/device/keys/${encodeURIComponent(id)}`);
}

// WEB_DEVICE: добавлено 2026-10-18 — выполнение агентских задач на ПК пользователя.

export interface DeviceProject {
  id: string;
  name: string;
}

export interface DeviceQueuedTask {
  taskId: string;
  chatId: string;
  prompt: string;
  createdAt: string;
}

export interface DeviceStatus {
  online: boolean;
  name: string | null;
  os: string | null;
  workspace: string | null;
  projects: DeviceProject[];
  queued: DeviceQueuedTask[];
}

export async function getDeviceStatus(): Promise<DeviceStatus> {
  const { data } = await http.get<Partial<DeviceStatus>>('/device/status');
  return {
    online: Boolean(data?.online),
    name: data?.name ?? null,
    os: data?.os ?? null,
    workspace: data?.workspace ?? null,
    projects: Array.isArray(data?.projects) ? data.projects : [],
    queued: Array.isArray(data?.queued) ? data.queued : [],
  };
}

/** Runs a task that is waiting for the PC in the server sandbox instead. */
export async function runQueuedTaskInSandbox(taskId: string): Promise<void> {
  await http.post(`/device/queue/${encodeURIComponent(taskId)}/sandbox`);
}

/** Cancels a task that is waiting for the PC. */
export async function cancelQueuedTask(taskId: string): Promise<void> {
  await http.delete(`/device/queue/${encodeURIComponent(taskId)}`);
}

// WORKSPACE_MENU: добавлено 2026-11-27 — «+ Добавить рабочее пространство…» в окне визуального
// десктопа. Папку выбирает сам пользователь в нативном диалоге на ПК: браузер не имеет доступа к
// файловой системе, поэтому веб лишь просит подключённый клиент открыть выбор папки, добавить её в
// projects.json и перерегистрировать список — новый проект виден после `device.refresh()`.
/**
 * Asks the connected PC to open its native folder picker and register the chosen folder.
 * Resolves when the event was delivered to the PC; `404 { error: 'DESKTOP_OFFLINE' }` when no PC is
 * connected. The new project appears in `getDeviceStatus()` after the desktop re-registers.
 */
export async function addWorkspace(): Promise<void> {
  await http.post('/device/workspace');
}

// MOBILE_APP: приложение для Android. Версия и ссылка на APK приходят из последнего релиза
// на GitHub, где лежит ассет conexy-android.apk.
export interface MobileClientInfo {
  downloadUrl: string;
  platform: string;
  /** Версия APK (тег последнего релиза), null — неизвестна. */
  version: string | null;
}

export async function getMobileClientInfo(): Promise<MobileClientInfo> {
  const { data } = await http.get<MobileClientInfo>('/device/client?platform=android');
  return data;
}
