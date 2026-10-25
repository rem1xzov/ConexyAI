import { useEffect, useRef } from 'react';

/**
 * CURSOR_TRAIL: короткоживущий след из мелких частиц за курсором на пустом стартовом экране.
 *
 * Почему так, а не иначе (решения из спецификации, все они про производительность):
 * - `<canvas>`, а не 90 DOM-узлов: один слой, layout не пересчитывается каждый кадр;
 * - `requestAnimationFrame` живёт только пока есть частицы — на пустом экране кадров нет;
 * - спавн по расстоянию и антидребезгу по времени, иначе на медленном движении частицы
 *   сбивались бы в одну кучу;
 * - цвета берутся из CSS-переменных (`--accent`, `--muted`), палитра не дублируется в JS;
 * - `devicePixelRatio` ограничен 2: выше — лишняя нагрузка без видимой разницы;
 * - след только на ПК (точный указатель) и не включается при `prefers-reduced-motion`.
 *
 * Слой стоит последним ребёнком `.app` (после `.main`) с `z-index: 1`: он покрывает весь экран,
 * включая сайдбар, но остаётся НИЖЕ модалок (они в том же стековом контексте с z-index 100+).
 */

type ParticleType = 'capsule' | 'cross' | 'molecule' | 'dot';

interface Particle {
  x: number;
  y: number;
  vx: number;
  vy: number;
  size: number;
  rot: number;
  spin: number;
  life: number;
  ttl: number;
  type: ParticleType;
  color: string;
  alpha: number;
}

const MAX_PARTICLES = 90;
/** Пикселей между частицами. */
const SPAWN_DISTANCE = 14;
/** Антидребезг, мс: клик и дрожь руки не дают комок частиц в одной точке. */
const SPAWN_INTERVAL = 32;
const DPR_CAP = 2;

function cssColor(name: string, fallback: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || fallback;
}

export function CursorTrail() {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)');
    if (reduce.matches) return;
    // Только точный указатель: на телефоне и планшете эффекта нет.
    if (!window.matchMedia('(pointer: fine)').matches) return;

    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    let accent = cssColor('--accent', '#c68bd4');
    let muted = cssColor('--muted', '#a79bae');

    const particles: Particle[] = [];
    let dpr = 1;
    let w = 0;
    let h = 0;
    let rafId = 0;
    let lastSpawn = 0;
    let lastX: number | null = null;
    let lastY = 0;

    const resize = () => {
      dpr = Math.min(window.devicePixelRatio || 1, DPR_CAP);
      w = window.innerWidth;
      h = window.innerHeight;
      canvas.width = Math.floor(w * dpr);
      canvas.height = Math.floor(h * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    };

    const pickType = (): ParticleType => {
      const r = Math.random();
      if (r < 0.34) return 'capsule';
      if (r < 0.58) return 'cross';
      if (r < 0.78) return 'molecule';
      return 'dot';
    };

    const spawn = (x: number, y: number) => {
      if (particles.length >= MAX_PARTICLES) return;
      particles.push({
        x,
        y,
        vx: (Math.random() - 0.5) * 0.35,
        vy: -0.15 - Math.random() * 0.35,
        size: 3 + Math.random() * 6,
        rot: Math.random() * Math.PI * 2,
        spin: (Math.random() - 0.5) * 0.02,
        life: 0,
        ttl: 700 + Math.random() * 400,
        type: pickType(),
        color: Math.random() < 0.6 ? accent : muted,
        alpha: 0.1 + Math.random() * 0.25,
      });
    };

    /** `roundRect` есть не везде (Chrome 99+, Safari 16+): без него — тот же контур через arcTo. */
    const capsulePath = (size: number, r: number) => {
      if (typeof ctx.roundRect === 'function') {
        ctx.roundRect(-size, -r, size * 2, r * 2, r);
        return;
      }
      ctx.moveTo(-size + r, -r);
      ctx.arcTo(size, -r, size, r, r);
      ctx.arcTo(size, r, -size, r, r);
      ctx.arcTo(-size, r, -size, -r, r);
      ctx.arcTo(-size, -r, -size + r, -r, r);
      ctx.closePath();
    };

    const drawCapsule = (p: Particle) => {
      const r = p.size * 0.5;
      ctx.beginPath();
      capsulePath(p.size, r);
      ctx.stroke();
      ctx.beginPath();
      ctx.moveTo(0, -r);
      ctx.lineTo(0, r);
      ctx.stroke();
    };

    const drawCross = (p: Particle) => {
      const t = p.size * 0.34;
      ctx.beginPath();
      ctx.rect(-t / 2, -p.size, t, p.size * 2);
      ctx.rect(-p.size, -t / 2, p.size * 2, t);
      ctx.fill();
    };

    const drawMolecule = (p: Particle) => {
      const r = p.size * 0.28;
      const points: [number, number][] = [
        [-p.size, 0],
        [p.size * 0.6, -p.size * 0.7],
        [p.size * 0.6, p.size * 0.7],
      ];
      ctx.beginPath();
      ctx.moveTo(points[0][0], points[0][1]);
      ctx.lineTo(points[1][0], points[1][1]);
      ctx.lineTo(points[2][0], points[2][1]);
      ctx.closePath();
      ctx.stroke();
      for (const [px, py] of points) {
        ctx.beginPath();
        ctx.arc(px, py, r, 0, Math.PI * 2);
        ctx.fill();
      }
    };

    const drawDot = (p: Particle) => {
      ctx.beginPath();
      ctx.arc(0, 0, p.size * 0.3, 0, Math.PI * 2);
      ctx.fill();
    };

    const frame = () => {
      rafId = 0;
      // clearRect, а не полупрозрачный fillRect: второй копил бы шлейф и непрозрачность.
      ctx.clearRect(0, 0, w, h);
      for (let i = particles.length - 1; i >= 0; i--) {
        const p = particles[i];
        p.life += 16;
        if (p.life >= p.ttl) {
          particles.splice(i, 1);
          continue;
        }

        p.x += p.vx;
        p.y += p.vy;
        p.rot += p.spin;

        const fade = 1 - p.life / p.ttl;
        // save/restore на каждую частицу обязательны: rotate и globalAlpha иначе протекают.
        ctx.save();
        ctx.translate(p.x, p.y);
        ctx.rotate(p.rot);
        ctx.globalAlpha = p.alpha * fade;
        ctx.strokeStyle = p.color;
        ctx.fillStyle = p.color;
        ctx.lineWidth = 1.2;

        if (p.type === 'capsule') drawCapsule(p);
        else if (p.type === 'cross') drawCross(p);
        else if (p.type === 'molecule') drawMolecule(p);
        else drawDot(p);

        ctx.restore();
      }
      if (particles.length > 0) rafId = window.requestAnimationFrame(frame);
    };

    const onMove = (event: PointerEvent) => {
      // Только мышь: тач и стилус след не оставляют.
      if (event.pointerType !== 'mouse') return;
      const x = event.clientX;
      const y = event.clientY;
      const now = performance.now();
      if (now - lastSpawn < SPAWN_INTERVAL) return;

      if (lastX !== null && Math.hypot(x - lastX, y - lastY) < SPAWN_DISTANCE) return;
      lastX = x;
      lastY = y;
      lastSpawn = now;
      spawn(x, y);
      if (!rafId) rafId = window.requestAnimationFrame(frame);
    };

    const onVisibility = () => {
      if (!document.hidden) return;
      particles.length = 0;
      if (rafId) {
        window.cancelAnimationFrame(rafId);
        rafId = 0;
      }
      ctx.clearRect(0, 0, w, h);
    };

    // Тема и инкогнито меняют палитру: след не должен оставаться в старой.
    const themeObserver = new MutationObserver(() => {
      accent = cssColor('--accent', '#c68bd4');
      muted = cssColor('--muted', '#a79bae');
    });

    let resizeTimer = 0;
    const onResize = () => {
      window.clearTimeout(resizeTimer);
      resizeTimer = window.setTimeout(resize, 150);
    };

    resize();
    window.addEventListener('pointermove', onMove, { passive: true });
    window.addEventListener('resize', onResize);
    document.addEventListener('visibilitychange', onVisibility);
    themeObserver.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-theme', 'data-incognito'],
    });

    return () => {
      window.clearTimeout(resizeTimer);
      if (rafId) window.cancelAnimationFrame(rafId);
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('resize', onResize);
      document.removeEventListener('visibilitychange', onVisibility);
      themeObserver.disconnect();
      particles.length = 0;
    };
  }, []);

  return <canvas className="cursor-trail" ref={canvasRef} aria-hidden="true" />;
}
