import { useEffect, useRef } from 'react';

/**
 * MOBILE_KEYBOARD: what the on-screen keyboard does to the layout and to the composer's focus.
 *
 * Two separate jobs share one measurement, because both need the same signal:
 *
 * 1. `interactive-widget=resizes-content` (index.html) shrinks the layout viewport on Android, so the
 *    flex layout already lifts everything by itself. Safari ignores that hint and only shrinks the
 *    visual viewport, which leaves the composer under the keyboard — that is what the `--kb-inset`
 *    custom property below compensates for. Where the hint works the measured gap is ~0, so the two
 *    compose instead of stacking.
 *
 * 2. KEYBOARD_CLOSE: closing the keyboard with the Android back button (or the back gesture) leaves
 *    the textarea focused — no blur event ever arrives — so the start screen stayed in its focused
 *    layout with the composer pinned to the bottom until the user tapped empty space. Watching the
 *    viewport grow back is the only reliable signal that the keyboard is gone.
 */

/**
 * Anything smaller than this is browser chrome (the URL/toolbar), not a keyboard.
 * MOBILE_VIEWPORT_FIX: порог поднят с 60px. Тулбар Safari отнимает 60–120px и присутствует почти
 * постоянно, поэтому с прежним порогом его принимали за клавиатуру: `--kb-inset` становился
 * ненулевым, `.app` получал нижний отступ, и главный экран «съезжал» вверх при обычном обновлении
 * страницы. Настоящая экранная клавиатура всегда выше 150px, так что детект не страдает.
 */
const MIN_KEYBOARD_PX = 150;
/** Closing the keyboard hands back roughly this much height in one go. */
const KEYBOARD_CLOSE_PX = 100;
/** A rotation changes the width too, and its height jump is not a keyboard closing. */
const WIDTH_SLOP_PX = 8;

interface KeyboardInsetOptions {
  enabled: boolean;
  /**
   * The keyboard went away, so the composer is no longer being typed into. Called unconditionally on
   * that transition — some Android builds blur the field themselves, so keying this off
   * `document.activeElement` would miss exactly the case this exists for.
   */
  onKeyboardClosed?: () => void;
}

/**
 * Whether the last viewport sample is the keyboard going away.
 *
 * Two things are ruled out on purpose: the very first sample (there is nothing to compare against),
 * and a change of width — a rotation hands back height too, but the composer's focus has nothing to
 * do with it.
 */
export function isKeyboardClosed(
  height: number,
  previousHeight: number,
  width: number,
  previousWidth: number,
): boolean {
  if (previousHeight === 0) return false;
  if (height - previousHeight <= KEYBOARD_CLOSE_PX) return false;
  if (previousWidth !== 0 && Math.abs(width - previousWidth) > WIDTH_SLOP_PX) return false;
  return true;
}

export function useKeyboardInset({ enabled, onKeyboardClosed }: KeyboardInsetOptions): void {
  // Kept in a ref so the listeners are attached once, not re-attached on every render.
  const closedRef = useRef(onKeyboardClosed);
  closedRef.current = onKeyboardClosed;

  useEffect(() => {
    if (!enabled) return;
    const viewport = window.visualViewport;
    if (!viewport) return;

    const root = document.documentElement;
    let frame = 0;
    let previousHeight = 0;
    let previousWidth = 0;

    /**
     * The height the user can actually see: `resizes-content` shrinks the layout viewport, Safari
     * only shrinks the visual one, so the smaller of the two covers both models.
     */
    const visibleHeight = () => Math.min(window.innerHeight, viewport.height);

    const apply = () => {
      frame = 0;

      // VIEWPORT_HEIGHT: держим высоту приложения ровно по видимой области. `100dvh` на телефоне
      // может «застыть» устаревшим — возврат из фона/bfcache, показ адресной строки при возврате на
      // сайт — и тогда низ макета (композер) уходил под экран. `visualViewport.height` всегда свежий,
      // поэтому именно из него считаем высоту `.app`, а --kb-inset остаётся для fixed-элементов.
      const visiblePx = Math.max(0, Math.round(viewport.height));
      if (visiblePx > 0) {
        root.style.setProperty('--app-height', `${visiblePx}px`);
      }

      // offsetTop is subtracted as well: when the keyboard opens, Safari shifts the visual viewport
      // up, and that shift is not part of the covered height.
      const inset = Math.max(0, Math.round(window.innerHeight - viewport.height - viewport.offsetTop));
      root.style.setProperty('--kb-inset', inset > MIN_KEYBOARD_PX ? `${inset}px` : '0px');

      const height = visibleHeight();
      const width = window.innerWidth;
      const closed = isKeyboardClosed(height, previousHeight, width, previousWidth);
      previousHeight = height;
      previousWidth = width;

      if (!closed) return;

      // Drop the focus explicitly: the browser skipped the blur, and the composer may still own it.
      // Only our own field — a transcript message being edited in a textarea keeps its focus.
      const active = document.activeElement;
      if (active instanceof HTMLElement && active.hasAttribute('data-composer-input')) {
        active.blur();
      }
      // MOBILE_LAYOUT: при закрытии клавиатуры Safari иногда оставляет визуальный вьюпорт смещённым
      // вниз — тогда нижняя часть макета (композер) уезжает под нижнюю панель/индикатор. Сброс
      // прокрутки документа возвращает вьюпорт на место; когда он и так на месте — это no-op.
      try {
        window.scrollTo(0, 0);
      } catch {
        /* scroll may be unavailable in odd embedders; the reset is best-effort */
      }
      closedRef.current?.();
    };

    const schedule = () => {
      if (frame === 0) frame = window.requestAnimationFrame(apply);
    };

    apply();
    viewport.addEventListener('resize', schedule);
    viewport.addEventListener('scroll', schedule);
    // VIEWPORT_HEIGHT: возврат на сайт из фона восстанавливает страницу из bfcache без resize —
    // без этих слушателей высота осталась бы устаревшей, и композер уехал бы под экран.
    window.addEventListener('pageshow', schedule);
    window.addEventListener('orientationchange', schedule);
    document.addEventListener('visibilitychange', schedule);
    return () => {
      viewport.removeEventListener('resize', schedule);
      viewport.removeEventListener('scroll', schedule);
      window.removeEventListener('pageshow', schedule);
      window.removeEventListener('orientationchange', schedule);
      document.removeEventListener('visibilitychange', schedule);
      if (frame) window.cancelAnimationFrame(frame);
      root.style.removeProperty('--kb-inset');
      root.style.removeProperty('--app-height');
    };
  }, [enabled]);
}
