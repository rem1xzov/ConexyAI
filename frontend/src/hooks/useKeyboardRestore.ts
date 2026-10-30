import { useEffect } from 'react';

/**
 * KEYBOARD_RESTORE: как в DeepSeek — если человек печатал и смахнул шторку уведомлений (переключить
 * трек), после её закрытия клавиатура открывается сама, курсор на прежнем месте.
 *
 * Шторка забирает у окна фокус (`window.blur`) раньше, чем система прячет клавиатуру, поэтому
 * состояние запоминается именно в этот момент: позже обработчик закрытия клавиатуры
 * (useKeyboardInset) уже снимет фокус с композера.
 *
 * В приложении возврат запускает MainActivity.onWindowFocusChanged(true) через
 * `window.__conexyRestoreKeyboard()`: программный focus() в WebView не всегда поднимает
 * клавиатуру, поэтому нативная часть дополнительно вызывает showSoftInput. На сайте (и в старых
 * APK без этого вызова) работает обычное событие `focus` окна.
 */

interface Snapshot {
  el: HTMLTextAreaElement;
  start: number;
  end: number;
}

declare global {
  interface Window {
    /** Called by the Android shell when the window gets focus back; true when the composer was refocused. */
    __conexyRestoreKeyboard?: () => boolean;
  }
}

function composerOf(node: Element | null): HTMLTextAreaElement | null {
  return node instanceof HTMLTextAreaElement && node.hasAttribute('data-composer-input') ? node : null;
}

function hasNativeShell(): boolean {
  const cap = (window as { Capacitor?: { isNativePlatform?: () => boolean } }).Capacitor;
  return Boolean(cap?.isNativePlatform?.());
}

export function useKeyboardRestore(enabled: boolean): void {
  useEffect(() => {
    if (!enabled) return;

    let snapshot: Snapshot | null = null;

    const remember = () => {
      const el = composerOf(document.activeElement);
      snapshot = el && !el.disabled ? { el, start: el.selectionStart, end: el.selectionEnd } : null;
    };

    const restore = (): boolean => {
      const saved = snapshot;
      snapshot = null;
      if (!saved || !saved.el.isConnected || saved.el.disabled) return false;
      const { el, start, end } = saved;
      // Если поле формально всё ещё в фокусе, повторный focus() браузер проигнорирует и IME не
      // запросит — поэтому сначала коротко снимаем фокус.
      if (document.activeElement === el) el.blur();
      window.requestAnimationFrame(() => {
        window.setTimeout(() => {
          el.focus({ preventScroll: true });
          const length = el.value.length;
          el.setSelectionRange(Math.min(start, length), Math.min(end, length));
        }, 30);
      });
      return true;
    };

    // Человек сам ушёл из поля (отправил, тапнул по ленте) — восстанавливать нечего.
    const onFocusOut = (e: FocusEvent) => {
      if (document.hasFocus() && composerOf(e.target as Element | null)) snapshot = null;
    };

    const native = hasNativeShell();
    const onWindowFocus = () => {
      if (!native) restore();
    };
    const onVisibility = () => {
      if (document.visibilityState === 'hidden') snapshot = null; // ушли в другое приложение
    };

    window.__conexyRestoreKeyboard = restore;
    window.addEventListener('blur', remember);
    window.addEventListener('focus', onWindowFocus);
    document.addEventListener('focusout', onFocusOut);
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      if (window.__conexyRestoreKeyboard === restore) delete window.__conexyRestoreKeyboard;
      window.removeEventListener('blur', remember);
      window.removeEventListener('focus', onWindowFocus);
      document.removeEventListener('focusout', onFocusOut);
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [enabled]);
}
