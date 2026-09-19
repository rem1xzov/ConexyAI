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

function effectiveTheme(theme: Theme): 'light' | 'dark' {
  if (theme === 'system') {
    return window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
  }
  return theme;
}

export function applyTheme(theme: Theme): void {
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
