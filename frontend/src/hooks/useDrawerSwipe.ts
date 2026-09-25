import { useEffect, useRef, type RefObject } from 'react';

/**
 * MOBILE_DRAWER: edge-swipe for the mobile chat drawer.
 *
 * The transform is written straight to the DOM node while the finger moves (never through React
 * state), so a drag costs no re-renders and stays on the compositor. React state is only touched
 * once, on release, to commit the final open/closed value.
 */

/** A swipe must start this close to the left edge to open the drawer. */
export const EDGE_ZONE_PX = 30;
/** px per ms: above this the release counts as a flick and wins over the finger's position. */
export const FLICK_VELOCITY = 0.5;
/** Fraction of the drawer width that must be travelled to open it, when starting closed. */
export const OPEN_RATIO = 0.35;
/** ...and to close it, when starting open: the mirror image, so 35% of the way back. */
export const CLOSE_RATIO = 1 - OPEN_RATIO;
/** How far one axis must beat the other before the gesture is claimed by the drawer. */
const AXIS_SLOP_PX = 4;
/** A little over the 0.25s CSS transition: after this the inline styles are dropped. */
const SETTLE_MS = 280;
const EASE = 'cubic-bezier(0.16, 1, 0.3, 1)';

/** Drawer travel as a fraction (0 closed .. 1 open) for a horizontal drag of `dx` pixels. */
export function drawerProgress(startProgress: number, dx: number, width: number): number {
  if (width <= 0) return startProgress;
  return Math.min(1, Math.max(0, startProgress + dx / width));
}

/**
 * Whether the drawer ends up open. A flick decides on its own; otherwise the position does, with
 * the threshold mirrored around the middle so closing needs the same 35% of travel as opening.
 */
export function shouldDrawerOpen(progress: number, startProgress: number, velocity: number): boolean {
  if (velocity > FLICK_VELOCITY) return true;
  if (velocity < -FLICK_VELOCITY) return false;
  return progress > (startProgress === 1 ? CLOSE_RATIO : OPEN_RATIO);
}

/**
 * True when the gesture belongs to a horizontally scrollable element under the finger rather than
 * to the drawer — otherwise swiping a wide code block or a scrolled row would close the sidebar.
 */
export function belongsToHorizontalScroller(target: EventTarget | null, dx: number): boolean {
  let el = target instanceof HTMLElement ? target : null;
  while (el && el !== document.body) {
    const overflowX = window.getComputedStyle(el).overflowX;
    if (/(auto|scroll)/.test(overflowX) && el.scrollWidth > el.clientWidth + 1) {
      if (dx < 0) return el.scrollLeft > 0;
      return el.scrollLeft + el.clientWidth < el.scrollWidth;
    }
    el = el.parentElement;
  }
  return false;
}

interface DragState {
  startX: number;
  startY: number;
  startedAt: number;
  lastX: number;
  lastTime: number;
  width: number;
  /** 1 when the drag started from the open drawer, 0 when from the closed one. */
  startProgress: number;
  progress: number;
  axis: 'undecided' | 'horizontal' | 'ignored';
}

interface DrawerSwipeOptions {
  /** Off on desktop: the sidebar there is a static column, not a drawer. */
  enabled: boolean;
  open: boolean;
  onOpen: () => void;
  onClose: () => void;
  panelRef: RefObject<HTMLElement | null>;
  backdropRef: RefObject<HTMLElement | null>;
}

export function useDrawerSwipe(options: DrawerSwipeOptions): void {
  // The listeners are attached once and read everything through this ref, so a prop change never
  // tears them down mid-gesture.
  const latest = useRef(options);
  latest.current = options;

  const dragRef = useRef<DragState | null>(null);
  const settleTimer = useRef<number | null>(null);

  useEffect(() => {
    // Percentages are relative to the panel itself, which is exactly viewport-wide in the mobile
    // layout — so the resting states stay responsive without measuring anything.
    const paint = (progress: number, animate: boolean) => {
      const panel = latest.current.panelRef.current;
      if (panel) {
        panel.style.transition = animate ? `transform 0.25s ${EASE}` : 'none';
        panel.style.willChange = 'transform';
        panel.style.transform = `translateX(${(progress - 1) * 100}%)`;
      }
      const backdrop = latest.current.backdropRef.current;
      if (backdrop) {
        backdrop.style.transition = animate ? 'opacity 0.25s ease' : 'none';
        backdrop.style.opacity = String(progress);
      }
    };

    const handBackToCss = () => {
      const panel = latest.current.panelRef.current;
      if (panel) {
        panel.style.transition = '';
        panel.style.transform = '';
        panel.style.willChange = '';
      }
      const backdrop = latest.current.backdropRef.current;
      if (backdrop) {
        backdrop.style.transition = '';
        backdrop.style.opacity = '';
      }
    };

    const settle = (open: boolean) => {
      paint(open ? 1 : 0, true);
      if (settleTimer.current !== null) window.clearTimeout(settleTimer.current);
      settleTimer.current = window.setTimeout(() => {
        settleTimer.current = null;
        handBackToCss();
      }, SETTLE_MS);
    };

    const onTouchStart = (e: TouchEvent) => {
      if (!latest.current.enabled || e.touches.length !== 1) return;
      const touch = e.touches[0];
      // Opening is an edge gesture; closing works from anywhere on the panel.
      const opening = !latest.current.open;
      if (opening && touch.clientX > EDGE_ZONE_PX) return;

      // A new gesture takes over from whatever the last one left behind.
      if (settleTimer.current !== null) {
        window.clearTimeout(settleTimer.current);
        settleTimer.current = null;
      }

      const now = performance.now();
      dragRef.current = {
        startX: touch.clientX,
        startY: touch.clientY,
        startedAt: now,
        lastX: touch.clientX,
        lastTime: now,
        width: window.innerWidth,
        startProgress: opening ? 0 : 1,
        progress: opening ? 0 : 1,
        axis: 'undecided',
      };
    };

    const onTouchMove = (e: TouchEvent) => {
      const drag = dragRef.current;
      if (!drag) return;
      const touch = e.touches[0];
      if (!touch) return;

      const dx = touch.clientX - drag.startX;
      const dy = touch.clientY - drag.startY;

      if (drag.axis === 'undecided') {
        if (Math.abs(dx) > Math.abs(dy) + AXIS_SLOP_PX) {
          drag.axis = belongsToHorizontalScroller(e.target, dx) ? 'ignored' : 'horizontal';
        } else if (Math.abs(dy) > Math.abs(dx) + AXIS_SLOP_PX) {
          drag.axis = 'ignored'; // a vertical scroll: leave it to the browser
        }
        if (drag.axis !== 'horizontal') return;
      }

      // The drawer owns the gesture now: no page scroll, no browser back-swipe.
      e.preventDefault();
      drag.progress = drawerProgress(drag.startProgress, dx, drag.width);
      drag.lastX = touch.clientX;
      drag.lastTime = performance.now();
      paint(drag.progress, false);
    };

    const onTouchEnd = () => {
      const drag = dragRef.current;
      dragRef.current = null;
      if (!drag || drag.axis !== 'horizontal') return;

      // Velocity over the whole gesture (px/ms) — steadier than the last frame alone, which would
      // read as a huge flick whenever the finger pauses just before lifting.
      const span = Math.max(drag.lastTime - drag.startedAt, 1);
      const velocity = (drag.lastX - drag.startX) / span;

      const open = shouldDrawerOpen(drag.progress, drag.startProgress, velocity);
      if (open !== latest.current.open) {
        if (open) latest.current.onOpen();
        else latest.current.onClose();
      }
      settle(open);
    };

    const onTouchCancel = () => {
      const drag = dragRef.current;
      dragRef.current = null;
      if (drag?.axis === 'horizontal') settle(latest.current.open);
    };

    window.addEventListener('touchstart', onTouchStart, { passive: true });
    // Non-passive: the horizontal drag has to cancel the page's own scrolling.
    window.addEventListener('touchmove', onTouchMove, { passive: false });
    window.addEventListener('touchend', onTouchEnd);
    window.addEventListener('touchcancel', onTouchCancel);
    return () => {
      window.removeEventListener('touchstart', onTouchStart);
      window.removeEventListener('touchmove', onTouchMove);
      window.removeEventListener('touchend', onTouchEnd);
      window.removeEventListener('touchcancel', onTouchCancel);
      if (settleTimer.current !== null) window.clearTimeout(settleTimer.current);
    };
  }, []);
}
