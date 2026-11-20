import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { ChatSessionKind } from '../types/chat';
import { CloseIcon, SearchIcon, TerminalIcon, UserIcon } from './Icons';
import { ChatGlyphIcon, SlidersGlyphIcon } from './MobileIcons';

/**
 * UI_EXTRAS: библиотека промптов, онбординг, мобильная нижняя навигация и палитра быстрых действий.
 * Все четыре — самостоятельные оверлеи/панели; App только передаёт колбэки и открывает их.
 */

// ---------------------------------------------------------------- prompt library

export interface SavedPrompt {
  id: string;
  title: string;
  text: string;
}

const PROMPT_STORAGE = 'conexy_prompts_v1';

function loadPrompts(): SavedPrompt[] {
  try {
    const raw = localStorage.getItem(PROMPT_STORAGE);
    const parsed = raw ? JSON.parse(raw) : null;
    return Array.isArray(parsed) ? parsed.filter((p) => p && typeof p.id === 'string') : [];
  } catch {
    return [];
  }
}

function storePrompts(prompts: SavedPrompt[]) {
  try {
    localStorage.setItem(PROMPT_STORAGE, JSON.stringify(prompts));
  } catch {
    // Storage is best-effort (private mode); the UI still works for this session.
  }
}

const DEFAULT_PROMPTS: SavedPrompt[] = [
  { id: 'p-review', title: 'Ревью кода', text: 'Сделай ревью этого кода: баги, безопасность, читаемость. Дай список правок по приоритету.' },
  { id: 'p-tests', title: 'Тесты', text: 'Напиши тесты для этого кода: сначала список случаев, затем рабочий тестовый файл.' },
  { id: 'p-explain', title: 'Объясни код', text: 'Объясни, что делает этот код, построчно и простыми словами.' },
];

export function PromptLibrary({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const [prompts, setPrompts] = useState<SavedPrompt[]>(() => {
    const stored = loadPrompts();
    return stored.length ? stored : DEFAULT_PROMPTS;
  });
  const [title, setTitle] = useState('');
  const [text, setText] = useState('');

  useEffect(() => {
    if (open) storePrompts(prompts);
  }, [prompts, open]);

  if (!open) return null;

  function insert(prompt: string) {
    window.dispatchEvent(new CustomEvent('conexy:insert-prompt', { detail: { text: prompt } }));
    onClose();
  }

  function add() {
    const ttl = title.trim();
    const body = text.trim();
    if (!ttl || !body) return;
    setPrompts((prev) => [{ id: `p-${Date.now()}`, title: ttl, text: body }, ...prev]);
    setTitle('');
    setText('');
  }

  return (
    <div className="artifact-overlay" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="prompt-lib" role="dialog" aria-modal="true">
        <div className="artifact-modal__head">
          <div className="artifact-modal__title">{t('extras.promptsTitle')}</div>
          <button className="icon-btn" type="button" onClick={onClose} aria-label={t('common.close')}>
            <CloseIcon size={16} />
          </button>
        </div>

        <ul className="prompt-lib__list">
          {prompts.map((p) => (
            <li key={p.id} className="prompt-lib__item">
              <button type="button" className="prompt-lib__use" onClick={() => insert(p.text)}>
                <span className="prompt-lib__name">{p.title}</span>
                <span className="prompt-lib__text">{p.text}</span>
              </button>
              <button
                type="button"
                className="prompt-lib__del"
                aria-label={t('extras.promptDelete')}
                onClick={() => setPrompts((prev) => prev.filter((x) => x.id !== p.id))}
              >
                ×
              </button>
            </li>
          ))}
        </ul>

        <div className="prompt-lib__add">
          <input
            className="dialog-input"
            placeholder={t('extras.promptName')}
            value={title}
            onChange={(e) => setTitle(e.target.value)}
          />
          <textarea
            className="dialog-input settings-textarea"
            rows={3}
            placeholder={t('extras.promptText')}
            value={text}
            onChange={(e) => setText(e.target.value)}
          />
          <button className="dialog-btn dialog-btn--primary" type="button" onClick={add} disabled={!title.trim() || !text.trim()}>
            {t('extras.promptAdd')}
          </button>
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- onboarding

const ONBOARDING_STORAGE = 'conexy_onboarding_v1';

type OnboardingState = { hidden?: boolean };

function loadOnboarding(): OnboardingState {
  try {
    return JSON.parse(localStorage.getItem(ONBOARDING_STORAGE) || '{}') as OnboardingState;
  } catch {
    return {};
  }
}

export function OnboardingChecklist({
  onOpenSettings,
  onNewChat,
}: {
  onOpenSettings: (section: 'personalization' | 'memory' | 'desktop') => void;
  onNewChat: () => void;
}) {
  const { t } = useTranslation();
  const [hidden, setHidden] = useState(() => loadOnboarding().hidden === true);
  if (hidden) return null;

  const steps: { id: string; title: string; hint: string; action: () => void }[] = [
    { id: 'chat', title: t('extras.onbChat'), hint: t('extras.onbChatHint'), action: onNewChat },
    { id: 'name', title: t('extras.onbName'), hint: t('extras.onbNameHint'), action: () => onOpenSettings('personalization') },
    { id: 'memory', title: t('extras.onbMemory'), hint: t('extras.onbMemoryHint'), action: () => onOpenSettings('memory') },
    { id: 'app', title: t('extras.onbApp'), hint: t('extras.onbAppHint'), action: () => onOpenSettings('desktop') },
  ];

  return (
    <div className="onboarding">
      <div className="onboarding__head">
        <span className="onboarding__title">{t('extras.onbTitle')}</span>
        <button
          type="button"
          className="onboarding__hide"
          onClick={() => {
            setHidden(true);
            try {
              localStorage.setItem(ONBOARDING_STORAGE, JSON.stringify({ hidden: true }));
            } catch {
              // ignore
            }
          }}
        >
          {t('extras.onbHide')}
        </button>
      </div>
      <ul className="onboarding__list">
        {steps.map((s) => (
          <li key={s.id}>
            <button type="button" className="onboarding__step" onClick={s.action}>
              <span className="onboarding__dot" aria-hidden="true" />
              <span className="onboarding__meta">
                <span className="onboarding__name">{s.title}</span>
                <span className="onboarding__hint">{s.hint}</span>
              </span>
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}

// ---------------------------------------------------------------- mobile bottom nav

export function MobileBottomNav({
  activeTab,
  onTabChange,
  onSettings,
}: {
  activeTab: ChatSessionKind;
  onTabChange: (tab: ChatSessionKind) => void;
  onSettings: () => void;
}) {
  const { t } = useTranslation();
  // MOBILE_NAV: иконки тонкие и спокойные, как в Claude: чат, терминал агента, ученики, настройки.
  const tabs: { kind: ChatSessionKind; label: string; icon: JSX.Element }[] = [
    { kind: 'chat', label: t('sidebar.tabs.chat'), icon: <ChatGlyphIcon size={21} /> },
    { kind: 'projects', label: t('sidebar.tabs.agent'), icon: <TerminalIcon size={21} /> },
    { kind: 'students', label: t('sidebar.tabs.students'), icon: <UserIcon size={21} /> },
  ];
  return (
    <nav className="mobile-nav" aria-label={t('extras.mobileNav')}>
      {tabs.map((tab) => (
        <button
          key={tab.kind}
          type="button"
          className={`mobile-nav__btn${activeTab === tab.kind ? ' mobile-nav__btn--active' : ''}`}
          onClick={() => onTabChange(tab.kind)}
        >
          {tab.icon}
          <span>{tab.label}</span>
        </button>
      ))}
      <button type="button" className="mobile-nav__btn" onClick={onSettings}>
        <SlidersGlyphIcon size={21} />
        <span>{t('account.settings')}</span>
      </button>
    </nav>
  );
}

// ---------------------------------------------------------------- quick actions (Ctrl+K)

export interface QuickAction {
  id: string;
  label: string;
  hint?: string;
  run: () => void;
}

export function QuickActionsPalette({
  open,
  onClose,
  actions,
}: {
  open: boolean;
  onClose: () => void;
  actions: QuickAction[];
}) {
  const { t } = useTranslation();
  const [query, setQuery] = useState('');
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (open) {
      setQuery('');
      setTimeout(() => inputRef.current?.focus(), 30);
    }
  }, [open]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q ? actions.filter((a) => a.label.toLowerCase().includes(q)) : actions;
  }, [actions, query]);

  if (!open) return null;

  const shortcuts: { keys: string; label: string }[] = [
    { keys: 'Ctrl+K', label: t('extras.scPalette') },
    { keys: 'Enter', label: t('extras.scSend') },
    { keys: 'Shift+Enter', label: t('extras.scNewline') },
    { keys: 'Esc', label: t('extras.scClose') },
    { keys: 'Ctrl+P', label: t('extras.scPrompts') },
  ];

  return (
    <div className="artifact-overlay" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="quick-palette" role="dialog" aria-modal="true">
        <div className="quick-palette__search">
          <SearchIcon size={16} />
          <input
            ref={inputRef}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('extras.palettePlaceholder')}
            onKeyDown={(e) => {
              if (e.key === 'Escape') onClose();
              if (e.key === 'Enter' && filtered[0]) {
                filtered[0].run();
                onClose();
              }
            }}
          />
        </div>
        <ul className="quick-palette__list">
          {filtered.map((a) => (
            <li key={a.id}>
              <button
                type="button"
                className="quick-palette__item"
                onClick={() => {
                  a.run();
                  onClose();
                }}
              >
                <span>{a.label}</span>
                {a.hint && <span className="quick-palette__hint">{a.hint}</span>}
              </button>
            </li>
          ))}
          {filtered.length === 0 && <li className="quick-palette__empty">{t('extras.paletteEmpty')}</li>}
        </ul>
        <div className="quick-palette__shortcuts">
          {shortcuts.map((s) => (
            <span key={s.keys} className="quick-palette__sc">
              <kbd>{s.keys}</kbd> {s.label}
            </span>
          ))}
        </div>
      </div>
    </div>
  );
}
