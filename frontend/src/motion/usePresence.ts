import { useEffect, useState } from 'react';
import { duration } from './motionConfig';

/**
 * PRESENCE: держит элемент в DOM на время exit-анимации.
 *
 * Использование:
 *   const p = usePresence(open, duration.menuOut);
 *   {p.mounted && <div className={p.closing ? 'is-closing' : ''}>...</div>}
 *
 * Никаких layout-свойств: компонент сам проигрывает CSS-анимацию на
 * opacity/transform, а размонтирование происходит после её окончания.
 */
export function usePresence(open: boolean, exitMs: number = duration.screen): { mounted: boolean; closing: boolean } {
  const [mounted, setMounted] = useState(open);
  const [closing, setClosing] = useState(false);

  useEffect(() => {
    let timer: number | undefined;
    if (open) {
      setMounted(true);
      setClosing(false);
    } else if (mounted) {
      setClosing(true);
      timer = window.setTimeout(() => {
        setMounted(false);
        setClosing(false);
      }, Math.max(0, exitMs));
    }
    return () => {
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, [open, mounted, exitMs]);

  return { mounted, closing };
}
