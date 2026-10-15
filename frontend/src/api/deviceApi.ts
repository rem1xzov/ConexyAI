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
