import { useEffect, useState } from 'react';

// PLAIN_CODE: добавлено 2026-10-12 — настройка представления «Персонализация»: выключить цветные
// «чипы» инлайн-кода и ключевых терминов в ответах модели и рисовать их обычным текстом с
// выделением жирным. Хранится локально (как тема), применяется атрибутом на <html> — CSS реагирует
// на него. Отдельно от серверных prefs: это чисто оформление, ехать на сервер ему незачем.
const STORAGE_KEY = 'conexy.plainCode';

function read(): boolean {
  try {
    return localStorage.getItem(STORAGE_KEY) === '1';
  } catch {
    return false;
  }
}

let current = read();
const listeners = new Set<(value: boolean) => void>();

/** Applies the current value as a root attribute; call once at startup and on every change. */
export function applyDisplayPrefs(): void {
  document.documentElement.setAttribute('data-plain-code', current ? 'true' : 'false');
}

export function getPlainCode(): boolean {
  return current;
}

export function setPlainCode(value: boolean): void {
  current = value;
  try {
    localStorage.setItem(STORAGE_KEY, value ? '1' : '0');
  } catch {
    // storage may be unavailable
  }
  applyDisplayPrefs();
  listeners.forEach((listener) => listener(current));
}

/** Reactively returns whether inline code / terms are rendered as plain bold white. */
export function usePlainCode(): boolean {
  const [state, setState] = useState(current);
  useEffect(() => {
    const listener = (value: boolean) => setState(value);
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  }, []);
  return state;
}
