import { useEffect, useState } from 'react';

// EDITOR_SETTINGS: добавлено 2026-10-05 — пользовательские настройки редактора IDE (размер шрифта,
// размер табов) и переназначение горячих клавиш. Хранятся в localStorage и рассылаются подписчикам,
// поэтому изменение применяется сразу во всех открытых редакторах.

export type HotkeyAction = 'save' | 'format' | 'terminal' | 'palette' | 'run' | 'search';

export const HOTKEY_ACTIONS: HotkeyAction[] = ['save', 'format', 'terminal', 'palette', 'run', 'search'];

export interface EditorSettings {
  /** Editor font size in px (10..24). */
  fontSize: number;
  /** Indentation width: 2 or 4 spaces. */
  tabSize: 2 | 4;
  /** Combo per action, e.g. "Ctrl+S" or "Shift+Alt+F". */
  hotkeys: Record<HotkeyAction, string>;
}

export const DEFAULT_SETTINGS: EditorSettings = {
  fontSize: 14,
  tabSize: 2,
  hotkeys: {
    save: 'Ctrl+S',
    format: 'Shift+Alt+F',
    terminal: 'Ctrl+`',
    palette: 'Ctrl+K',
    run: 'Ctrl+F5',
    search: 'Ctrl+Shift+F',
  },
};

const STORAGE_KEY = 'conexy.editorSettings';

function clampFont(value: unknown): number {
  const n = typeof value === 'number' && Number.isFinite(value) ? Math.round(value) : DEFAULT_SETTINGS.fontSize;
  return Math.min(24, Math.max(10, n));
}

function load(): EditorSettings {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return DEFAULT_SETTINGS;
    const parsed = JSON.parse(raw) as Partial<EditorSettings>;
    return {
      fontSize: clampFont(parsed.fontSize),
      tabSize: parsed.tabSize === 4 ? 4 : 2,
      hotkeys: { ...DEFAULT_SETTINGS.hotkeys, ...(parsed.hotkeys ?? {}) },
    };
  } catch {
    return DEFAULT_SETTINGS;
  }
}

let current: EditorSettings = load();
const listeners = new Set<(settings: EditorSettings) => void>();

export function getEditorSettings(): EditorSettings {
  return current;
}

export interface EditorSettingsPatch {
  fontSize?: number;
  tabSize?: 2 | 4;
  hotkeys?: Partial<Record<HotkeyAction, string>>;
}

export function updateEditorSettings(patch: EditorSettingsPatch): void {
  current = {
    ...current,
    fontSize: patch.fontSize !== undefined ? clampFont(patch.fontSize) : current.fontSize,
    tabSize: patch.tabSize === 4 ? 4 : patch.tabSize === 2 ? 2 : current.tabSize,
    hotkeys: { ...current.hotkeys, ...(patch.hotkeys ?? {}) },
  };
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(current));
  } catch {
    /* storage may be unavailable */
  }
  listeners.forEach((listener) => listener(current));
}

export function resetEditorSettings(): void {
  updateEditorSettings({ ...DEFAULT_SETTINGS, hotkeys: { ...DEFAULT_SETTINGS.hotkeys } });
}

export function useEditorSettings(): EditorSettings {
  const [state, setState] = useState(current);
  useEffect(() => {
    const listener = (settings: EditorSettings) => setState(settings);
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  }, []);
  return state;
}

// --- hotkey combos ---------------------------------------------------------------------------

function keyName(e: KeyboardEvent): string | null {
  if (e.code === 'Backquote') return '`';
  if (e.code === 'Space' || e.key === ' ') return 'Space';
  const k = e.key;
  if (k === 'Control' || k === 'Shift' || k === 'Alt' || k === 'Meta') return null;
  if (k.length === 1) return k.toUpperCase();
  if (/^F\d{1,2}$/.test(k)) return k;
  if (k.startsWith('Arrow') || ['Enter', 'Escape', 'Tab', 'Backspace', 'Delete', 'Home', 'End'].includes(k)) return k;
  return null;
}

/** Builds the canonical combo string of a keydown (modifier order: Ctrl, Meta, Alt, Shift). */
export function comboFromEvent(e: KeyboardEvent): string | null {
  const key = keyName(e);
  if (!key) return null;
  const parts: string[] = [];
  if (e.ctrlKey) parts.push('Ctrl');
  if (e.metaKey) parts.push('Meta');
  if (e.altKey) parts.push('Alt');
  if (e.shiftKey) parts.push('Shift');
  parts.push(key);
  return parts.join('+');
}

export function matchesHotkey(e: KeyboardEvent, combo: string): boolean {
  return combo.length > 0 && comboFromEvent(e) === combo;
}
