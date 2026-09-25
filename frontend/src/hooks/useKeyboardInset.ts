import { useEffect } from 'react';

/**
 * MOBILE_KEYBOARD: the on-screen keyboard must not cover the composer.
 *
 * `interactive-widget=resizes-content` (index.html) shrinks the layout viewport on Android, so the
 * flex layout already lifts everything by itself. Safari ignores that hint and only shrinks the
 * visual viewport, which leaves the composer under the keyboard — that is what this fallback
 * measures. The gap is published as the `--kb-inset` custom property, which the layout subtracts at
 * the bottom. On browsers where the hint works the measured gap is ~0, so the two compose.
 */

/** Anything smaller than this is browser chrome (URL bar), not a keyboard. */
const MIN_KEYBOARD_PX = 60;

export function useKeyboardInset(enabled: boolean): void {
  useEffect(() => {
    if (!enabled) return;
    const viewport = window.visualViewport;
    if (!viewport) return;

    const root = document.documentElement;
    let frame = 0;

    const apply = () => {
      frame = 0;
      // offsetTop is subtracted as well: when the keyboard opens, Safari shifts the visual viewport
      // up, and that shift is not part of the covered height.
      const inset = Math.max(0, Math.round(window.innerHeight - viewport.height - viewport.offsetTop));
      root.style.setProperty('--kb-inset', inset > MIN_KEYBOARD_PX ? `${inset}px` : '0px');
    };

    const schedule = () => {
      if (frame === 0) frame = window.requestAnimationFrame(apply);
    };

    apply();
    viewport.addEventListener('resize', schedule);
    viewport.addEventListener('scroll', schedule);
    return () => {
      viewport.removeEventListener('resize', schedule);
      viewport.removeEventListener('scroll', schedule);
      if (frame) window.cancelAnimationFrame(frame);
      root.style.removeProperty('--kb-inset');
    };
  }, [enabled]);
}
