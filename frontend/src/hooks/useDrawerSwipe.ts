import { useEffect, useRef, type RefObject } from 'react';

/**
 * MOBILE_DRAWER: swipe for the mobile chat drawer.
 *
 * PUSH_DRAWER: как в DeepSeek — панель не наезжает на чат, а выталкивает его вправо. Панель и
 * узлы из `pushRefs` (экран чата, кнопка меню, затемнение) сдвигаются на один и тот же offset.
 *
 * The transform is written straight to the DOM node while the finger moves (never through React
 * state), so a drag costs no re-renders and stays on the compositor. React state is only touched
 * once, on release, to commit the final open/closed value.
 */

/**
 * Fraction of the viewport, measured from the left edge, where a swipe may start to open the drawer.
 * MOBILE_SWIPE_ZONE: PUSH_DRAWER — весь экран: горизонтальный свайп в любом месте тянет ящик.
 */
export const OPEN_SWIPE_ZONE_RATIO = 1;

/** True when a gesture starting at `clientX` is allowed to open the drawer. */
export function isInOpenSwipeZone(clientX: number, viewportWidth: number): boolean {
  return clientX <= viewportWidth * OPEN_SWIPE_ZONE_RATIO;
}

/** px per ms: above this the release counts as a flick and wins over the finger's position. */
export const FLICK_VELOCITY = 0.5;
/**
 * DRAWER_ACCIDENT: флик засчитывается, только если панель протянули хотя бы на этот путь (после мёртвой
 * зоны): резкое, но короткое касание не должно распахивать меню.
 */
export const MIN_FLICK_TRAVEL_PX = 45;
/**
 * DRAWER_ACCIDENT: мёртвая зона. Пока палец не ушёл по горизонтали дальше этого, экран стоит на месте.
 * После захвата сдвиг считается от точки захвата, так что рывка на эти пиксели нет.
 */
export const DEADZONE_PX = 18;
/** Vertical travel after which the touch is locked as a chat scroll. */
export const VERTICAL_LOCK_PX = 8;
/** |dx| must beat |dy| by this factor (tan 60°): the swipe is within 30° of horizontal. */
export const HORIZONTAL_RATIO = 1.73;
/** Fraction of the drawer width that must be travelled to open it, when starting closed. */
export const OPEN_RATIO = 0.35;
/** ...and to close it, when starting open: the mirror image, so 35% of the way back. */
export const CLOSE_RATIO = 1 - OPEN_RATIO;

/** A little over the 0.25s CSS transition: after this the inline styles are dropped. */
const SETTLE_MS = 280;
const EASE = 'cubic-bezier(0.22, 1, 0.36, 1)';
/**
 * DRAWER_STUCK: если жест начался, но ни touchend, ни touchcancel не пришёл (системный жест
 * Android, палец ушёл за край экрана), панель оставалась в промежуточном положении, и её
 * приходилось закрывать вручную. Через эту паузу без движения жест доводится до конца сам.
 */
const DRAG_WATCHDOG_MS = 900;

/** Drawer travel as a fraction (0 closed .. 1 open) for a horizontal drag of `dx` pixels. */
export function drawerProgress(startProgress: number, dx: number, width: number): number {
  if (width <= 0) return startProgress;
  return Math.min(1, Math.max(0, startProgress + dx / width));
}

/**
 * Whether the drawer ends up open. A flick decides on its own (if it travelled far enough);
 * otherwise the position does, with the threshold mirrored so closing needs the same 35% as opening.
 *
 * DRAWER_ACCIDENT: открытие требует осознанного жеста — короткий сдвиг панель не открывает.
 */
export function shouldDrawerOpen(
  progress: number,
  startProgress: number,
  velocity: number,
  travelPx = 0,
): boolean {
  const flick = Math.abs(travelPx) > MIN_FLICK_TRAVEL_PX;
  if (flick && velocity > FLICK_VELOCITY) return true;
  if (flick && velocity < -FLICK_VELOCITY) return false;
  return progress > (startProgress === 1 ? CLOSE_RATIO : OPEN_RATIO);
}

/**
 * Which axis a gesture belongs to, once it has moved far enough to tell. `null` means «ещё не ясно».
 *
 * DRAWER_ACCIDENT: вертикаль фиксируется рано и с запасом (любая диагональ — это чтение чата), а
 * горизонталь — только после мёртвой зоны и в пределах 30° от горизонтали.
 */
export function claimAxis(dx: number, dy: number): 'horizontal' | 'vertical' | null {
  const absX = Math.abs(dx);
  const absY = Math.abs(dy);
  if (absY > VERTICAL_LOCK_PX && absY >= absX * 0.6) return 'vertical';
  if (absX > DEADZONE_PX && absX > absY * HORIZONTAL_RATIO) return 'horizontal';
  return null;
}

/** True while an undecided gesture already leans horizontal: its moves must stay cancelable. */
function leansHorizontal(dx: number, dy: number): boolean {
  return Math.abs(dx) >= 3 && Math.abs(dx) > Math.abs(dy) * HORIZONTAL_RATIO;
}

/**
 * DRAWER_TABLE: касания, которые ящик не трогает вообще: таблицы, код, поля ввода, слайдеры и
 * всё, что помечено `data-no-drawer-swipe`.
 */
const NO_DRAWER_SELECTOR = [
  'table', 'pre', 'code', '.markdown__table', '.code-block',
  'input', 'textarea', 'select', '[contenteditable=""]', '[contenteditable="true"]',
  '[data-no-drawer-swipe]',
].join(',');

export function isNoDrawerTarget(target: EventTarget | null): boolean {
  const el = target instanceof Element ? target : null;
  if (!el) return false;
  return el.closest(NO_DRAWER_SELECTOR) !== null || belongsToHorizontalScroller(el);
}

/**
 * True when the gesture lands on (or inside) an element that scrolls horizontally — a wide table or a
 * code block. Such a swipe belongs to that element, never to the drawer.
 *
 * DRAWER_TABLE: раньше проверка зависела от направления и позиции прокрутки и была инвертирована для
 * свайпа влево: широкая таблица в самом начале (scrollLeft = 0), которую тянули влево, чтобы увидеть
 * право, считалась жестом ЯЩИКА — панель выезжала поверх прокручиваемой таблицы и застревала в
 * полуоткрытом виде. Теперь правило простое и предсказуемое: палец на горизонтальном скроллере —
 * жест ему, ящик не трогаем.
 */
export function belongsToHorizontalScroller(target: EventTarget | null): boolean {
  let el = target instanceof HTMLElement ? target : null;
  while (el && el !== document.body) {
    const overflowX = window.getComputedStyle(el).overflowX;
    if (/(auto|scroll)/.test(overflowX) && el.scrollWidth > el.clientWidth + 2) return true;
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
  /** How far the panel has been pulled out, in pixels (0 closed .. width open). */
  offsetPx: number;
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
  /** PUSH_DRAWER: nodes that travel right together with the panel. */
  pushRefs?: RefObject<HTMLElement | null>[];
}

export function useDrawerSwipe(options: DrawerSwipeOptions): void {
  // The listeners are attached once and read everything through this ref, so a prop change never
  // tears them down mid-gesture.
  const latest = useRef(options);
  latest.current = options;

  const dragRef = useRef<DragState | null>(null);
  const settleTimer = useRef<number | null>(null);
  const watchdogTimer = useRef<number | null>(null);

  useEffect(() => {
    const drawerWidth = () => latest.current.panelRef.current?.offsetWidth || window.innerWidth;
    const pushNodes = () =>
      (latest.current.pushRefs ?? []).map((ref) => ref.current).filter((n): n is HTMLElement => !!n);

    // The offset is written in pixels on top of the CSS -100%, so panel and pushed nodes always
    // move by exactly the same amount, whatever the drawer width resolves to.
    const paint = (offsetPx: number, animate: boolean) => {
      const x = Math.round(offsetPx);
      const transition = animate ? `transform 0.28s ${EASE}` : 'none';
      const panel = latest.current.panelRef.current;
      if (panel) {
        panel.style.transition = transition;
        panel.style.willChange = 'transform';
        panel.style.transform = `translate3d(calc(-100% + ${x}px), 0, 0)`;
        // While the drawer is out from under its edge it must accept touches, even mid-drag.
        panel.style.pointerEvents = 'auto';
      }
      for (const node of pushNodes()) {
        node.style.transition = transition;
        node.style.willChange = 'transform';
        node.style.transform = `translate3d(${x}px, 0, 0)`;
      }
      const backdrop = latest.current.backdropRef.current;
      if (backdrop) {
        backdrop.style.transition = animate ? `opacity 0.28s ${EASE}, transform 0.28s ${EASE}` : 'none';
        backdrop.style.opacity = String(Math.min(1, Math.max(0, offsetPx / Math.max(drawerWidth(), 1))));
      }
    };

    const handBackToCss = () => {
      const panel = latest.current.panelRef.current;
      if (panel) {
        panel.style.transition = '';
        panel.style.transform = '';
        panel.style.willChange = '';
        panel.style.pointerEvents = '';
      }
      for (const node of pushNodes()) {
        node.style.transition = '';
        node.style.transform = '';
        node.style.willChange = '';
      }
      const backdrop = latest.current.backdropRef.current;
      if (backdrop) {
        backdrop.style.transition = '';
        backdrop.style.opacity = '';
      }
    };

    const settle = (open: boolean) => {
      paint(open ? drawerWidth() : 0, true);
      if (settleTimer.current !== null) window.clearTimeout(settleTimer.current);
      settleTimer.current = window.setTimeout(() => {
        settleTimer.current = null;
        handBackToCss();
      }, SETTLE_MS);
    };

    const clearWatchdog = () => {
      if (watchdogTimer.current !== null) {
        window.clearTimeout(watchdogTimer.current);
        watchdogTimer.current = null;
      }
    };

    /**
     * DRAWER_STUCK: доводит активный жест до конца. Вызывается обычным touchend/touchcancel, а
     * также сторожем и потерей фокуса — чтобы панель не оставалась в промежуточном положении.
     */
    const finishDrag = () => {
      const drag = dragRef.current;
      dragRef.current = null;
      clearWatchdog();
      if (!drag || drag.axis !== 'horizontal') return;

      // Velocity over the whole gesture (px/ms) — steadier than the last frame alone, which would
      // read as a huge flick whenever the finger pauses just before lifting.
      const span = Math.max(drag.lastTime - drag.startedAt, 1);
      const velocity = (drag.lastX - drag.startX) / span;

      const open = shouldDrawerOpen(
        drag.offsetPx / Math.max(drag.width, 1),
        drag.startProgress,
        velocity,
        drag.lastX - drag.startX,
      );
      if (open !== latest.current.open) {
        if (open) latest.current.onOpen();
        else latest.current.onClose();
      }
      settle(open);
    };

    const armWatchdog = () => {
      clearWatchdog();
      watchdogTimer.current = window.setTimeout(() => {
        watchdogTimer.current = null;
        finishDrag();
      }, DRAG_WATCHDOG_MS);
    };

    const onTouchStart = (e: TouchEvent) => {
      if (!latest.current.enabled || e.touches.length !== 1) return;

      // DRAWER_STUCK: если предыдущий жест не получил своего touchend/touchcancel (системный жест,
      // палец ушёл за край), доводим его до конца ДО начала нового — иначе панель останется в
      // промежуточном положении, а новый жест уже не будет знать о ней.
      if (dragRef.current) finishDrag();

      // DRAWER_STUCK: не обрываем идущую анимацию возврата — доводим её сразу до конечного
      // положения. Раньше таймер просто отменялся, и если новый жест оказывался вертикальным,
      // панель замирала там, где её застали, и её приходилось убирать вручную.
      if (settleTimer.current !== null) {
        window.clearTimeout(settleTimer.current);
        settleTimer.current = null;
        handBackToCss();
      }

      const touch = e.touches[0];
      const opening = !latest.current.open;
      if (opening && !isInOpenSwipeZone(touch.clientX, window.innerWidth)) return;
      // DRAWER_TABLE: свайп по таблице, коду или полю ввода принадлежит им, а не ящику.
      if (isNoDrawerTarget(e.target)) return;

      const now = performance.now();
      const width = drawerWidth();
      dragRef.current = {
        startX: touch.clientX,
        startY: touch.clientY,
        startedAt: now,
        lastX: touch.clientX,
        lastTime: now,
        width,
        startProgress: opening ? 0 : 1,
        offsetPx: opening ? 0 : width,
        axis: 'undecided',
      };
    };

    const onTouchMove = (e: TouchEvent) => {
      const drag = dragRef.current;
      // DRAWER_ACCIDENT: a touch locked as a scroll stays a scroll until the finger lifts.
      if (!drag || drag.axis === 'ignored') return;
      const touch = e.touches[0];
      if (!touch) return;

      const dx = touch.clientX - drag.startX;
      const dy = touch.clientY - drag.startY;

      if (drag.axis === 'undecided') {
        const axis = claimAxis(dx, dy);
        if (axis === 'vertical') {
          drag.axis = 'ignored'; // a chat scroll: leave it to the browser until touchend
          return;
        }
        if (axis !== 'horizontal') {
          // Inside the dead zone the screen stays put, but a horizontal-leaning move is still
          // cancelled: once the browser commits to its own gesture, the drag can't be taken back.
          if (leansHorizontal(dx, dy) && e.cancelable) e.preventDefault();
          return;
        }
        // DRAWER_ACCIDENT: travel is measured from the capture point, so the panel starts at 0px
        // instead of jumping by the dead zone.
        drag.axis = 'horizontal';
        drag.startX = touch.clientX;
        drag.startedAt = performance.now();
      }

      // The drawer owns the gesture now: no page scroll, no browser back-swipe.
      if (e.cancelable) e.preventDefault();
      const progress = drawerProgress(drag.startProgress, touch.clientX - drag.startX, drag.width);
      drag.offsetPx = progress * drag.width;
      drag.lastX = touch.clientX;
      drag.lastTime = performance.now();
      paint(drag.offsetPx, false);
      armWatchdog();
    };

    const onTouchEnd = () => finishDrag();
    const onTouchCancel = () => finishDrag();

    // DRAWER_STUCK: если вкладка потеряла фокус или ушла в фон посреди жеста, touchend может не
    // прийти вовсе — доводим жест сами, чтобы панель не осталась висеть полуоткрытой.
    const onInterrupted = () => finishDrag();

    window.addEventListener('touchstart', onTouchStart, { passive: false });
    // Non-passive: the horizontal drag has to cancel the page's own scrolling.
    window.addEventListener('touchmove', onTouchMove, { passive: false });
    window.addEventListener('touchend', onTouchEnd);
    window.addEventListener('touchcancel', onTouchCancel);
    window.addEventListener('blur', onInterrupted);
    document.addEventListener('visibilitychange', onInterrupted);
    return () => {
      window.removeEventListener('touchstart', onTouchStart);
      window.removeEventListener('touchmove', onTouchMove);
      window.removeEventListener('touchend', onTouchEnd);
      window.removeEventListener('touchcancel', onTouchCancel);
      window.removeEventListener('blur', onInterrupted);
      document.removeEventListener('visibilitychange', onInterrupted);
      if (settleTimer.current !== null) window.clearTimeout(settleTimer.current);
      if (watchdogTimer.current !== null) window.clearTimeout(watchdogTimer.current);
    };
  }, []);
}
