import { useEffect, useState } from 'react';

export type Theme = 'light' | 'dark' | 'system';

const THEME_KEY = 'conexy_theme';

export function getStoredTheme(): Theme {
  try {
    const v = localStorage.getItem(THEME_KEY);
    if (v === 'light' || v === 'dark' || v === 'system') return v;
  } catch {
    // ignore
  }
  // Default to dark: the app was originally dark-only and the light theme is incomplete
  // (some Tailwind colors are hardcoded), so avoid surprising light-mode-OS users.
  return 'dark';
}

const LIGHT_QUERY = '(prefers-color-scheme: light)';

function systemQuery(): MediaQueryList | null {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function' ? window.matchMedia(LIGHT_QUERY) : null;
}

function effectiveTheme(theme: Theme): 'light' | 'dark' {
  if (theme === 'system') {
    return systemQuery()?.matches ? 'light' : 'dark';
  }
  return theme;
}

// THEME_SYSTEM_LIVE: добавлено 2026-09-24 (L4) — тема «Системная» раньше вычислялась один раз при
// применении и дальше не следила за ОС: переключение ОС в тёмный режим (или авто-смена вечером)
// оставляло приложение в старой теме до перезагрузки. Теперь один общий слушатель matchMedia
// переприменяет тему, пока выбрана именно «Системная».
let activeTheme: Theme | null = null;
let systemListenerAttached = false;

function onSystemThemeChange(): void {
  if (activeTheme === 'system') {
    document.documentElement.setAttribute('data-theme', effectiveTheme('system'));
  }
}

function ensureSystemListener(): void {
  if (systemListenerAttached) return;
  const mql = systemQuery();
  if (!mql) return;
  if (typeof mql.addEventListener === 'function') {
    mql.addEventListener('change', onSystemThemeChange);
  } else {
    // Safari < 14 only knows the deprecated API.
    (mql as MediaQueryList & { addListener: (cb: () => void) => void }).addListener(onSystemThemeChange);
  }
  systemListenerAttached = true;
}

export function applyTheme(theme: Theme): void {
  activeTheme = theme;
  ensureSystemListener();
  document.documentElement.setAttribute('data-theme', effectiveTheme(theme));
}

export function setTheme(theme: Theme): void {
  try {
    localStorage.setItem(THEME_KEY, theme);
  } catch {
    // ignore
  }
  applyTheme(theme);
}

/** Reactively returns the effective theme ('light' | 'dark'), updating when it changes. */
export function useEffectiveTheme(): 'light' | 'dark' {
  const [theme, setThemeState] = useState<'light' | 'dark'>(() =>
    document.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark',
  );

  useEffect(() => {
    const el = document.documentElement;
    const observer = new MutationObserver(() => {
      setThemeState(el.getAttribute('data-theme') === 'light' ? 'light' : 'dark');
    });
    observer.observe(el, { attributes: true, attributeFilter: ['data-theme'] });
    return () => observer.disconnect();
  }, []);

  return theme;
}
