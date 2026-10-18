import { useCallback, useEffect, useState } from 'react';
import { getDeviceStatus, listDeviceKeys, type DeviceStatus } from '../api/deviceApi';

// WEB_DEVICE: добавлено 2026-10-18 — состояние ПК пользователя для выбора «Песочница / Мой ПК».
const POLL_MS = 15_000;

export interface DeviceStatusState {
  status: DeviceStatus | null;
  /** False when the user has no active desktop key — the PC was never set up. */
  hasKey: boolean | null;
  refresh: () => Promise<DeviceStatus | null>;
}

/** Polls `GET /device/status` every 15 s (and on window focus) while `enabled`. */
export function useDeviceStatus(enabled: boolean): DeviceStatusState {
  const [status, setStatus] = useState<DeviceStatus | null>(null);
  const [hasKey, setHasKey] = useState<boolean | null>(null);

  const refresh = useCallback(async () => {
    try {
      const next = await getDeviceStatus();
      setStatus(next);
      return next;
    } catch {
      return null;
    }
  }, []);

  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    void refresh();
    listDeviceKeys()
      .then((keys) => {
        if (!cancelled) setHasKey(keys.some((k) => !k.revoked));
      })
      .catch(() => undefined);
    const timer = window.setInterval(() => {
      if (document.visibilityState === 'visible') void refresh();
    }, POLL_MS);
    const onFocus = () => void refresh();
    window.addEventListener('focus', onFocus);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
      window.removeEventListener('focus', onFocus);
    };
  }, [enabled, refresh]);

  return { status, hasKey, refresh };
}
