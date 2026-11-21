/**
 * HAPTICS
 * Безопасный тактильный отклик: Capacitor Haptics, если плагин подключён,
 * иначе — короткая вибрация браузера. На устройствах без поддержки — no-op.
 *
 * Никогда не бросает исключений: отклик не должен ломать отправку сообщений.
 */

export type HapticType = 'light' | 'selection' | 'medium';

const VIBRATE_MS: Record<HapticType, number | number[]> = {
  light: 8,
  selection: 5,
  medium: 18,
};

let lastAt = 0;

function capacitorHaptics(): {
  impact?: (opts: { style: string }) => Promise<void> | void;
  selectionStart?: () => Promise<void> | void;
} | null {
  try {
    const cap = (window as unknown as { Capacitor?: { Plugins?: Record<string, unknown> } }).Capacitor;
    const plugin = cap?.Plugins?.Haptics as
      | { impact?: (opts: { style: string }) => Promise<void> | void; selectionStart?: () => Promise<void> | void }
      | undefined;
    return plugin ?? null;
  } catch {
    return null;
  }
}

export function triggerHaptic(type: HapticType = 'light'): void {
  // Защита от «пулемёта» при быстрых тапах по списку.
  const now = Date.now();
  if (now - lastAt < 40) return;
  lastAt = now;

  try {
    const plugin = capacitorHaptics();
    if (plugin) {
      if (type === 'selection' && plugin.selectionStart) {
        void plugin.selectionStart();
        return;
      }
      if (plugin.impact) {
        void plugin.impact({ style: type === 'medium' ? 'MEDIUM' : 'LIGHT' });
        return;
      }
    }
    if (typeof navigator !== 'undefined' && typeof navigator.vibrate === 'function') {
      navigator.vibrate(VIBRATE_MS[type]);
    }
  } catch {
    // haptics — необязательный сахар, молча пропускаем.
  }
}
