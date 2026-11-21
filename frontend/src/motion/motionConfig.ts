/**
 * MOTION_CONFIG
 * Единый ритм анимаций ConexyAI.
 *
 * Только GPU-свойства: transform (translate3d / scale) и opacity.
 * CSS-переменные прокидываются в :root через installMotionVars(), поэтому
 * любой компонент может использовать var(--motion-*) и var(--ease-*).
 */

export const duration = {
  /** Мгновенные состояния (hover и т.п.). */
  instant: 0,
  /** Выход меню/подсказки. */
  fast: 110,
  /** Быстрый отклик нажатия. */
  press: 100,
  /** Появление меню/дропдауна. */
  menu: 150,
  /** Базовый fade. */
  base: 180,
  /** Появление бабла/панели действий. */
  bubble: 220,
  /** Переходы экранов/модалок. */
  screen: 240,
  /** Крупные переходы. */
  slow: 320,
} as const;

export const easing = {
  /** Основная кривая проекта: мягкий выход, как в Claude. */
  out: 'cubic-bezier(0.22, 1, 0.36, 1)',
  /** Вход. */
  in: 'cubic-bezier(0.4, 0, 1, 1)',
  /** Симметричный переход экранов. */
  inOut: 'cubic-bezier(0.65, 0, 0.35, 1)',
  /** Старая кривая приложения — оставляем для совместимости. */
  standard: 'cubic-bezier(0.2, 0.8, 0.2, 1)',
  /** Линейная для shimmer. */
  linear: 'linear',
} as const;

export const spring = {
  /** Доводка drawer с учётом velocity. */
  drawer: { stiffness: 350, damping: 32, mass: 1 },
  /** Меню/пилюли. */
  menu: { stiffness: 500, damping: 38, mass: 0.8 },
} as const;

export const gesture = {
  /** Свайп drawer можно начинать из любой точки основной области. */
  drawerEdge: 0,
  /** Доля ширины, после которой drawer доводится до открытого. */
  drawerCommit: 0.35,
  /** Минимальная скорость (px/ms), чтобы довести drawer даже при малом пути. */
  drawerVelocity: 0.35,
} as const;

/** CSS-переменные, которые обязаны совпадать с константами выше. */
export const motionCssVars: Record<string, string> = {
  '--motion-instant': `${duration.instant}ms`,
  '--motion-fast': `${duration.fast}ms`,
  '--motion-press': `${duration.press}ms`,
  '--motion-menu': `${duration.menu}ms`,
  '--motion-menu-out': `${duration.fast}ms`,
  '--motion-base': `${duration.base}ms`,
  '--motion-bubble': `${duration.bubble}ms`,
  '--motion-screen': `${duration.screen}ms`,
  '--motion-slow': `${duration.slow}ms`,
  '--ease-out': easing.out,
  '--ease-in': easing.in,
  '--ease-in-out': easing.inOut,
  '--ease-standard': easing.standard,
};

export function installMotionVars(root: HTMLElement = document.documentElement): void {
  if (typeof document === 'undefined') return;
  for (const [name, value] of Object.entries(motionCssVars)) {
    root.style.setProperty(name, value);
  }
}

/** Уважаем системную настройку «уменьшить движение». */
export function prefersReducedMotion(): boolean {
  if (typeof window === 'undefined' || !window.matchMedia) return false;
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}
