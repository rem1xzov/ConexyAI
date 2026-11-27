import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { switchToConsole } from '../api/conexyApi';
import { TerminalIcon } from './Icons';

// VISUAL_DESKTOP: добавлено 2026-11-27 — обратный путь из «визуального десктопа» в терминал.
//
// Десктопный клиент (ConexyAI Desktop) открывает эту же SPA в отдельном окне Chromium по пути
// `/desktop`. Из этого окна легко потерять терминал: чтобы вернуться, клиенту нужно свернуть
// визуальное окно. Кнопка просит сервер передать клиенту на ПК событие `SwitchToConsole`
// (`POST /api/device/console`); сам клиент и делает переключение. Стили — только утилиты Tailwind,
// цвета берутся из переменных темы, поэтому кнопка читается и в тёмной, и в светлой теме.
//
// Компонент рендерится ТОЛЬКО когда приложение открыто по пути `/desktop` (проверка в App.tsx по
// `isVisualDesktopPath`), и живёт в шапке — то есть виден без прокрутки на десктопе и на телефоне.

interface VisualDesktopConsoleButtonProps {
  /** Единственный тост приложения: им показываем результат переключения. */
  onToast: (message: string) => void;
}

const BUTTON_CLASS = [
  'inline-flex shrink-0 items-center gap-1.5 rounded-full',
  'border border-[color:var(--accent)] bg-[var(--bg-soft)]',
  'px-3 py-1.5 text-xs font-semibold leading-none text-[color:var(--accent)]',
  'shadow-sm transition-colors hover:bg-[var(--bg-hover)] hover:text-[color:var(--text)]',
  'focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[color:var(--accent)]',
  'disabled:cursor-progress disabled:opacity-60',
].join(' ');

export function VisualDesktopConsoleButton({ onToast }: VisualDesktopConsoleButtonProps) {
  const { t } = useTranslation();
  const [busy, setBusy] = useState(false);

  async function handleClick() {
    if (busy) return;
    setBusy(true);
    try {
      await switchToConsole();
      onToast(t('visualDesktop.switchedToConsole'));
    } catch (error) {
      const response = (
        error as { response?: { status?: number; data?: { error?: unknown } } } | null
      )?.response;
      const status = response?.status;
      const code = typeof response?.data?.error === 'string' ? response.data.error : '';
      if (status === 404) {
        // DESKTOP_OFFLINE — сервер знает эндпоинт, но клиент на ПК не в сети; пустой 404 — сервер
        // старше этой функции (эндпоинта ещё нет), и «ПК не подключён» ввести в заблуждение не должно.
        onToast(code ? t('visualDesktop.noDesktop') : t('visualDesktop.oldServer'));
      } else {
        onToast(t('visualDesktop.switchFailed'));
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <button
      type="button"
      onClick={() => void handleClick()}
      disabled={busy}
      aria-busy={busy}
      title={t('visualDesktop.backToConsoleHint')}
      className={BUTTON_CLASS}
    >
      {busy ? (
        <span
          className="h-3 w-3 shrink-0 animate-spin rounded-full border-2 border-current border-t-transparent"
          aria-hidden="true"
        />
      ) : (
        <TerminalIcon size={14} />
      )}
      <span className="whitespace-nowrap">
        {busy ? t('visualDesktop.switching') : t('visualDesktop.backToConsole')}
      </span>
    </button>
  );
}
