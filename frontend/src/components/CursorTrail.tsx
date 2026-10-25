import { useEffect, useRef } from 'react';

/**
 * CURSOR_TRAIL: на пустом стартовом экране за курсором остаются метки фирменного знака — две
 * стрелки, как на openai.com/codex. Только ПК: тач-устройства, узкие экраны и
 * `prefers-reduced-motion` эффект не получают.
 *
 * Рисуем на canvas, а не узлами DOM: метка появляется на каждые ~16 px движения мыши, и десятки
 * анимируемых элементов заставляли бы React перерисовывать дерево десятки раз в секунду.
 * Слой лежит ПОД контентом (как .glass-aura), поэтому след не перекрывает композер и текст.
 */

/** Ширина одной стрелки в CSS-пикселях; остальная геометрия — из viewBox логотипа (440×315). */
const MARK_WIDTH = 20;
const LOGO_UNIT = MARK_WIDTH / 440;
const CHEVRON_HEIGHT = 145 * LOGO_UNIT;
const CHEVRON_STEP = 170 * LOGO_UNIT;
/** Логотип шире, чем выше: центрируем знак на курсоре, а не вешаем его над точкой. */
const VERTICAL_CENTER = (145 * LOGO_UNIT - CHEVRON_STEP) / 2;
const STROKE = 66 * LOGO_UNIT;

const LIFETIME_MS = 850;
/** Через сколько пикселей пути появляется следующая метка. */
const SPAWN_DISTANCE = 16;
const MAX_MARKS = 44;
const PEAK_ALPHA = 0.55;

interface Mark {
  x: number;
  y: number;
  angle: number;
  born: number;
}

function readAccent(): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim();
  return value || '#c68bd4';
}

export function CursorTrail() {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    // Тач, маленький экран и «меньше движения» — без эффекта вовсе.
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
    if (!window.matchMedia('(pointer: fine)').matches) return;

    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    let accent = readAccent();
    let width = 0;
    let height = 0;
    let raf = 0;
    const marks: Mark[] = [];

    const resize = () => {
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      width = window.innerWidth;
      height = window.innerHeight;
      canvas.width = Math.round(width * dpr);
      canvas.height = Math.round(height * dpr);
      canvas.style.width = `${width}px`;
      canvas.style.height = `${height}px`;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      marks.length = 0;
    };

    const paintMark = (mark: Mark, alpha: number, scale: number) => {
      ctx.save();
      ctx.translate(mark.x, mark.y + VERTICAL_CENTER);
      // Стрелка «смотрит» туда, куда едет курсор: у знака остриё вверх, отсюда +90°.
      ctx.rotate(mark.angle);
      ctx.scale(scale, scale);
      ctx.globalAlpha = alpha;
      ctx.strokeStyle = accent;
      ctx.lineWidth = STROKE;
      ctx.lineCap = 'round';
      ctx.lineJoin = 'round';
      for (let i = 0; i < 2; i++) {
        const base = i * CHEVRON_STEP;
        ctx.beginPath();
        ctx.moveTo(-MARK_WIDTH / 2, base);
        ctx.lineTo(0, base - CHEVRON_HEIGHT);
        ctx.lineTo(MARK_WIDTH / 2, base);
        ctx.stroke();
      }
      ctx.restore();
    };

    const tick = (now: number) => {
      raf = 0;
      ctx.clearRect(0, 0, width, height);
      for (let i = marks.length - 1; i >= 0; i--) {
        const mark = marks[i];
        const age = (now - mark.born) / LIFETIME_MS;
        if (age >= 1) {
          marks.splice(i, 1);
          continue;
        }
        const fade = 1 - age;
        paintMark(mark, PEAK_ALPHA * fade * fade, 1 - age * 0.3);
      }
      if (marks.length > 0) raf = window.requestAnimationFrame(tick);
    };

    /** Метки вдоль отрезка пути: быстрый рывок мыши не оставляет дыр в следе. */
    const spawnLine = (fromX: number, fromY: number, toX: number, toY: number) => {
      const dx = toX - fromX;
      const dy = toY - fromY;
      const distance = Math.hypot(dx, dy);
      if (distance < 1) return;
      const angle = Math.atan2(dy, dx) + Math.PI / 2;
      const steps = Math.min(4, Math.max(1, Math.floor(distance / SPAWN_DISTANCE)));
      const now = performance.now();
      for (let i = 1; i <= steps; i++) {
        const t = i / steps;
        marks.push({
          x: fromX + dx * t,
          y: fromY + dy * t,
          angle,
          born: now + i * 6,
        });
      }
      while (marks.length > MAX_MARKS) marks.shift();
      if (!raf) raf = window.requestAnimationFrame(tick);
    };

    let lastX: number | null = null;
    let lastY = 0;

    const onMove = (event: PointerEvent) => {
      // На тач-экране и стилусе следа нет — только мышь.
      if (event.pointerType !== 'mouse') return;
      const x = event.clientX;
      const y = event.clientY;
      if (lastX === null) {
        lastX = x;
        lastY = y;
        return;
      }
      if (Math.hypot(x - lastX, y - lastY) < SPAWN_DISTANCE) return;
      spawnLine(lastX, lastY, x, y);
      lastX = x;
      lastY = y;
    };

    // Тема и инкогнито меняют --accent: след не должен остаться в старой палитре.
    const observer = new MutationObserver(() => {
      accent = readAccent();
    });

    resize();
    window.addEventListener('resize', resize);
    window.addEventListener('pointermove', onMove, { passive: true });
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-theme', 'data-incognito'],
    });

    return () => {
      if (raf) window.cancelAnimationFrame(raf);
      window.removeEventListener('resize', resize);
      window.removeEventListener('pointermove', onMove);
      observer.disconnect();
      marks.length = 0;
    };
  }, []);

  return <canvas className="cursor-trail" ref={canvasRef} aria-hidden="true" />;
}
