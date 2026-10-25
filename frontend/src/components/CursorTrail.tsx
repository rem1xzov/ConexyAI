import { useEffect, useRef } from 'react';

/**
 * CURSOR_TRAIL: волна из фирменных стрелок, идущая ровно за курсором на пустом стартовом экране
 * (как фон на openai.com/codex). Только ПК: тач, узкие экраны и `prefers-reduced-motion` эффект
 * не получают.
 *
 * Ключевые решения:
 * - Координаты вычитают положение самого канваса (`getBoundingClientRect`). Канвас живёт внутри
 *   `.main` (чтобы не попасть под его backdrop-filter), а события мыши приходят в координатах
 *   окна — без вычета след уезжал вправо на ширину сайдбара.
 * - Рисуем в canvas, а не DOM: метка появляется каждые несколько пикселей пути, десятки
 *   анимируемых узлов заставляли бы React перерисовываться десятки раз в секунду.
 * - Каждая метка «плывёт» поперёк движения по синусу — за курсором остаётся не пунктир, а волна.
 */
const MARK_WIDTH = 14;
const LOGO_UNIT = MARK_WIDTH / 440;
const CHEVRON_HEIGHT = 145 * LOGO_UNIT;
const CHEVRON_STEP = 170 * LOGO_UNIT;
/** Логотип выше, чем кажется: центрируем знак на точке, а не вешаем его над курсором. */
const VERTICAL_CENTER = (145 * LOGO_UNIT - CHEVRON_STEP) / 2;
const STROKE = 62 * LOGO_UNIT;

const LIFETIME_MS = 650;
/** Через сколько пикселей пути появляется следующая метка: редкие значки, без плотного ковра. */
const SPAWN_DISTANCE = 22;
const MAX_MARKS = 26;
/** Едва видный след: волна читается как фон, а не как элемент интерфейса. */
const PEAK_ALPHA = 0.22;
/** Размах поперечной волны в пикселях. */
const WAVE_AMPLITUDE = 5;

interface Mark {
  x: number;
  y: number;
  angle: number;
  born: number;
  /** Сдвиг фазы: соседние метки колеблются не синхронно, получается бегущая волна. */
  phase: number;
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
      const rect = canvas.getBoundingClientRect();
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      width = Math.max(1, Math.round(rect.width));
      height = Math.max(1, Math.round(rect.height));
      canvas.width = Math.round(width * dpr);
      canvas.height = Math.round(height * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      marks.length = 0;
    };

    const paintMark = (mark: Mark, alpha: number, scale: number, wobble: number) => {
      ctx.save();
      ctx.translate(mark.x, mark.y + VERTICAL_CENTER);
      // Стрелка «смотрит» туда, куда едет курсор: у знака остриё вверх, отсюда +90°.
      ctx.rotate(mark.angle);
      // Поперёк движения — синусоида: хвост идёт волной.
      ctx.translate(wobble, 0);
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
        // Затухание к концу хвоста + бегущая поперечная волна, гаснущая вместе с меткой.
        const wobble = Math.sin(age * 5.5 + mark.phase) * WAVE_AMPLITUDE * fade;
        paintMark(mark, PEAK_ALPHA * fade * fade, 0.85 + 0.25 * fade, wobble);
      }
      if (marks.length > 0) raf = window.requestAnimationFrame(tick);
    };

    /** Метки вдоль отрезка пути: быстрый рывок мыши не оставляет дыр в волне. */
    const spawnLine = (fromX: number, fromY: number, toX: number, toY: number, now: number) => {
      const dx = toX - fromX;
      const dy = toY - fromY;
      const distance = Math.hypot(dx, dy);
      if (distance < 1) return;
      const angle = Math.atan2(dy, dx) + Math.PI / 2;
      const steps = Math.min(8, Math.max(1, Math.floor(distance / SPAWN_DISTANCE)));
      for (let i = 1; i <= steps; i++) {
        const t = i / steps;
        marks.push({
          x: fromX + dx * t,
          y: fromY + dy * t,
          angle,
          born: now + i * 4,
          phase: (marks.length + i) * 0.8,
        });
      }
      while (marks.length > MAX_MARKS) marks.shift();
      if (!raf) raf = window.requestAnimationFrame(tick);
    };

    let lastX: number | null = null;
    let lastY = 0;

    const onMove = (event: PointerEvent) => {
      // На тач-экране и стилусе волны нет — только мышь.
      if (event.pointerType !== 'mouse') return;
      // Канвас лежит внутри .main, поэтому координаты окна переводим в его собственные.
      const rect = canvas.getBoundingClientRect();
      const x = event.clientX - rect.left;
      const y = event.clientY - rect.top;
      if (lastX === null) {
        lastX = x;
        lastY = y;
        return;
      }
      if (Math.hypot(x - lastX, y - lastY) < SPAWN_DISTANCE) return;
      spawnLine(lastX, lastY, x, y, performance.now());
      lastX = x;
      lastY = y;
    };

    const onLeave = () => {
      lastX = null;
    };

    // Тема и инкогнито меняют --accent: волна не должна остаться в старой палитре.
    const themeObserver = new MutationObserver(() => {
      accent = readAccent();
    });
    // Ширина панели меняется при сворачивании сайдбара — размер холста должен следовать за ней.
    const sizeObserver = new ResizeObserver(resize);

    resize();
    sizeObserver.observe(canvas);
    window.addEventListener('resize', resize);
    window.addEventListener('pointermove', onMove, { passive: true });
    document.addEventListener('pointerleave', onLeave);
    themeObserver.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-theme', 'data-incognito'],
    });

    return () => {
      if (raf) window.cancelAnimationFrame(raf);
      sizeObserver.disconnect();
      window.removeEventListener('resize', resize);
      window.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerleave', onLeave);
      themeObserver.disconnect();
      marks.length = 0;
    };
  }, []);

  return <canvas className="cursor-trail" ref={canvasRef} aria-hidden="true" />;
}
