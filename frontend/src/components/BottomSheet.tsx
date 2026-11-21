import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from 'react';
import { createPortal } from 'react-dom';
import { duration, easing, spring } from '../motion/motionConfig';
import { usePresence } from '../motion/usePresence';
import { registerSheetBack } from '../motion/sheetBackStack';
import { triggerHaptic } from '../utils/haptics';

/**
 * BOTTOM_SHEET
 * Универсальный нативный нижний лист вместо десктопных дропдаунов.
 *
 * Умеет:
 *  - выезжать снизу на пружине и закрываться с exit-анимацией;
 *  - тянуться вниз 1:1 за пальцем и закрываться свайпом (drag-to-dismiss);
 *  - закрываться тапом по backdrop и по Escape;
 *  - отдавать light-хаптик при открытии;
 *  - не анимировать layout-свойства (только transform/opacity).
 *
 * Системный Android Back: родитель должен вызвать `onClose` в своём back-обработчике
 * (в App уже есть общий стек слоёв); компонент дополнительно ловит Escape.
 */
export interface BottomSheetProps {
  open: boolean;
  onClose: () => void;
  /** Заголовок над контентом; необязателен. */
  title?: ReactNode;
  /** Подпись для скринридеров. */
  ariaLabel?: string;
  children: ReactNode;
  /** Дополнительный класс панели. */
  className?: string;
  /** Показывать ли drag handle (по умолчанию да). */
  showHandle?: boolean;
}

const CLOSE_DISTANCE_RATIO = 0.28;
const CLOSE_VELOCITY = 0.55; // px/ms

function prefersReducedMotion(): boolean {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia('(prefers-reduced-motion: reduce)').matches
    : false;
}

export function BottomSheet({
  open,
  onClose,
  title,
  ariaLabel,
  children,
  className = '',
  showHandle = true,
}: BottomSheetProps) {
  const presence = usePresence(open, duration.screen);
  const panelRef = useRef<HTMLDivElement>(null);
  const startYRef = useRef(0);
  const startTimeRef = useRef(0);
  const draggingRef = useRef(false);
  const offsetRef = useRef(0);
  const springFrameRef = useRef<number | null>(null);
  const [dragging, setDragging] = useState(false);

  // ХАПТИК: лёгкий отклик в момент открытия + системный Back закрывает верхний лист.
  useEffect(() => {
    if (!open) return;
    triggerHaptic('light');
    return registerSheetBack(onClose);
  }, [open, onClose]);

  // Escape закрывает лист.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  // Пока лист открыт, страница под ним не скроллится.
  useEffect(() => {
    if (!open) return;
    const prev = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = prev;
    };
  }, [open]);

  const stopSpring = useCallback(() => {
    if (springFrameRef.current !== null) {
      cancelAnimationFrame(springFrameRef.current);
      springFrameRef.current = null;
    }
  }, []);

  // Пружинная доводка до target (0 — открыт, height — закрыт).
  const springTo = useCallback(
    (target: number, from: number, velocityPxPerMs: number, done?: () => void) => {
      stopSpring();
      if (prefersReducedMotion()) {
        if (panelRef.current) panelRef.current.style.transform = `translate3d(0, ${target}px, 0)`;
        done?.();
        return;
      }
      const { stiffness, damping, mass } = spring.drawer;
      let x = from;
      let v = velocityPxPerMs * 1000;
      let last = performance.now();
      const step = (now: number) => {
        const dt = Math.min(0.032, Math.max(0.001, (now - last) / 1000));
        last = now;
        const a = (-stiffness * (x - target) - damping * v) / mass;
        v += a * dt;
        x += v * dt;
        if (panelRef.current) panelRef.current.style.transform = `translate3d(0, ${Math.max(0, x)}px, 0)`;
        if (Math.abs(x - target) < 0.5 && Math.abs(v) < 6) {
          if (panelRef.current) panelRef.current.style.transform = `translate3d(0, ${target}px, 0)`;
          springFrameRef.current = null;
          done?.();
          return;
        }
        springFrameRef.current = requestAnimationFrame(step);
      };
      springFrameRef.current = requestAnimationFrame(step);
    },
    [stopSpring],
  );

  const finishDrag = useCallback(
    (velocityPxPerMs: number) => {
      if (!draggingRef.current) return;
      draggingRef.current = false;
      setDragging(false);
      const height = panelRef.current?.offsetHeight ?? window.innerHeight;
      const shouldClose = offsetRef.current > height * CLOSE_DISTANCE_RATIO || velocityPxPerMs > CLOSE_VELOCITY;
      if (shouldClose) {
        springTo(height, offsetRef.current, velocityPxPerMs, () => {
          offsetRef.current = 0;
          onClose();
        });
      } else {
        springTo(0, offsetRef.current, velocityPxPerMs, () => {
          offsetRef.current = 0;
        });
      }
    },
    [onClose, springTo],
  );

  const onPointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (!open || e.button !== 0) return;
    stopSpring();
    draggingRef.current = true;
    setDragging(true);
    startYRef.current = e.clientY;
    startTimeRef.current = performance.now();
    offsetRef.current = 0;
    e.currentTarget.setPointerCapture?.(e.pointerId);
  };

  const onPointerMove = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (!draggingRef.current) return;
    const dy = Math.max(0, e.clientY - startYRef.current);
    offsetRef.current = dy;
    if (panelRef.current) panelRef.current.style.transform = `translate3d(0, ${dy}px, 0)`;
  };

  const onPointerUp = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (!draggingRef.current) return;
    const elapsed = Math.max(1, performance.now() - startTimeRef.current);
    const velocity = offsetRef.current / elapsed;
    finishDrag(velocity);
  };

  useEffect(() => () => stopSpring(), [stopSpring]);

  if (!presence.mounted) return null;

  const reduced = prefersReducedMotion();

  return createPortal(
    <div className={`bottom-sheet-root${presence.closing ? ' is-closing' : ''}`}>
      <div
        className="bottom-sheet-backdrop"
        onClick={onClose}
        aria-hidden="true"
      />
      <div
        ref={panelRef}
        className={`bottom-sheet-panel${dragging ? ' is-dragging' : ''} ${className}`.trim()}
        role="dialog"
        aria-modal="true"
        aria-label={ariaLabel}
        style={reduced ? { animation: 'none' } : undefined}
      >
        {showHandle && (
          <div
            className="bottom-sheet-handle"
            onPointerDown={onPointerDown}
            onPointerMove={onPointerMove}
            onPointerUp={onPointerUp}
            onPointerCancel={onPointerUp}
          >
            <span className="bottom-sheet-handle__bar" />
          </div>
        )}
        {title && <div className="bottom-sheet-title">{title}</div>}
        <div className="bottom-sheet-body">{children}</div>
      </div>
    </div>,
    document.body,
  );
}

/** Тайминги/кривые для CSS-части листа (совпадают с motionConfig). */
export const BOTTOM_SHEET_TIMING = {
  enter: `${duration.screen}ms`,
  exit: `${duration.fast}ms`,
  easing: easing.out,
} as const;
