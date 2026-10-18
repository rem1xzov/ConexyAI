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
