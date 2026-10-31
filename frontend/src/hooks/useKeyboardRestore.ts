import { useEffect } from 'react';

/**
 * KEYBOARD_RESTORE: как в DeepSeek — если человек печатал и смахнул шторку уведомлений, после её
 * закрытия клавиатура открывается сама, курсор на прежнем месте.
 *
 * Решение «печатал ли» принимает Android-оболочка (MainActivity): она видит саму клавиатуру и знает,
 * была ли та открыта в момент потери фокуса окна. Страница не может это знать надёжно — система
 * прячет клавиатуру раньше, чем приходит `window.blur`, и к этому моменту поле уже без фокуса.
 * Поэтому здесь только:
 *  - постоянно запоминается последнее положение курсора в композере;
 *  - `window.__conexyRestoreKeyboard()` возвращает фокус в композер на это место.
 * На сайте (без оболочки) — тот же возврат по `window.focus`, если поле было в фокусе при уходе.
 */

declare global {
  interface Window {
    /** Called by the Android shell when the window gets focus back; true when the composer was refocused. */
    __conexyRestoreKeyboard?: () => boolean;
  }
}

function composerOf(node: EventTarget | Element | null): HTMLTextAreaElement | null {
  return node instanceof HTMLTextAreaElement && node.hasAttribute('data-composer-input') ? node : null;
}

function findComposer(): HTMLTextAreaElement | null {
  return composerOf(document.querySelector('textarea[data-composer-input]'));
}

function hasNativeShell(): boolean {
  const cap = (window as { Capacitor?: { isNativePlatform?: () => boolean } }).Capacitor;
  return Boolean(cap?.isNativePlatform?.());
}

export function useKeyboardRestore(enabled: boolean): void {
  useEffect(() => {
    if (!enabled) return;

    let caret: { start: number; end: number } | null = null;
    let typingWhenLeft = false;

    const track = () => {
      const el = composerOf(document.activeElement);
      if (el) caret = { start: el.selectionStart, end: el.selectionEnd };
    };

    const restore = (): boolean => {
      const el = findComposer();
      if (!el || el.disabled) return false;
      const saved = caret ?? { start: el.value.length, end: el.value.length };
      // Если поле формально ещё в фокусе, повторный focus() браузер проигнорирует и IME не
      // запросит — поэтому сначала коротко снимаем фокус.
      if (document.activeElement === el) el.blur();
      window.requestAnimationFrame(() => {
        window.setTimeout(() => {
          el.focus({ preventScroll: true });
          const length = el.value.length;
          el.setSelectionRange(Math.min(saved.start, length), Math.min(saved.end, length));
        }, 30);
      });
      return true;
    };

    const native = hasNativeShell();
    const onWindowBlur = () => {
      typingWhenLeft = composerOf(document.activeElement) !== null;
    };
    const onWindowFocus = () => {
      if (!native && typingWhenLeft) restore();
      typingWhenLeft = false;
    };

    window.__conexyRestoreKeyboard = restore;
    document.addEventListener('selectionchange', track);
    document.addEventListener('input', track, true);
    document.addEventListener('focusout', track, true);
    window.addEventListener('blur', onWindowBlur);
    window.addEventListener('focus', onWindowFocus);
    return () => {
      if (window.__conexyRestoreKeyboard === restore) delete window.__conexyRestoreKeyboard;
      document.removeEventListener('selectionchange', track);
      document.removeEventListener('input', track, true);
      document.removeEventListener('focusout', track, true);
      window.removeEventListener('blur', onWindowBlur);
      window.removeEventListener('focus', onWindowFocus);
    };
  }, [enabled]);
}
