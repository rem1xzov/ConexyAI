import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Theme } from '../theme';
import {
  PREFERENCE_MAX_LENGTH,
  clearMemory,
  deleteMemoryFact,
  getMemory,
  getPreferences,
  savePreferences,
  setMemoryEnabled,
  type MemoryFact,
  type UserPreferences,
} from '../api/userApi';
import { humanError } from '../utils/humanError';
import { ConfirmDialog } from './Dialog';
import { CloseIcon, TrashIcon } from './Icons';

export type SettingsSection = 'general' | 'personalization' | 'memory';

interface SettingsModalProps {
  theme: Theme;
  language: string;
  onThemeChange: (theme: Theme) => void;
  onLanguageChange: (lang: string) => void;
  onClose: () => void;
  /** Section to open first (defaults to "general"). */
  initialSection?: SettingsSection;
}

const THEMES: { value: Theme; key: string }[] = [
  { value: 'light', key: 'settings.themeLight' },
  { value: 'dark', key: 'settings.themeDark' },
  { value: 'system', key: 'settings.themeSystem' },
];

const LANGS: { value: string; key: string }[] = [
  { value: 'ru', key: 'settings.langRu' },
  { value: 'en', key: 'settings.langEn' },
];

const SECTIONS: { value: SettingsSection; key: string }[] = [
  { value: 'general', key: 'prefs.tabGeneral' },
  { value: 'personalization', key: 'prefs.tabPersonalization' },
  { value: 'memory', key: 'memory.tab' },
];

function statusOf(e: unknown): number | undefined {
  return (e as { response?: { status?: number } })?.response?.status;
}

function errorCodeOf(e: unknown): string | undefined {
  const data = (e as { response?: { data?: unknown } })?.response?.data;
  if (data && typeof data === 'object' && typeof (data as { error?: unknown }).error === 'string') {
    return (data as { error: string }).error;
  }
  return undefined;
}

// ---------------------------------------------------------------------------------------------
// CUSTOM_INSTRUCTIONS: добавлено 2026-09-24 (ТЗ-2 §5, контракт C-7) — «Обо мне» и «Как ConexyAI
// должен отвечать». Оба поля подмешиваются сервером в системный промпт каждого режима.
// ---------------------------------------------------------------------------------------------

interface PersonalizationSectionProps {
  active: boolean;
  onDirtyChange: (dirty: boolean) => void;
}

function PersonalizationSection({ active, onDirtyChange }: PersonalizationSectionProps) {
  const { t } = useTranslation();
  const [loaded, setLoaded] = useState<UserPreferences | null>(null);
  const [aboutMe, setAboutMe] = useState('');
  const [responseStyle, setResponseStyle] = useState('');
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedAt, setSavedAt] = useState<number | null>(null);
  const requestedRef = useRef(false);

  async function load() {
    setLoading(true);
    setLoadError(null);
    try {
      const prefs = await getPreferences();
      setLoaded(prefs);
      setAboutMe(prefs.aboutMe);
      setResponseStyle(prefs.responseStyle);
    } catch (e) {
      setLoadError(humanError(e, t));
    } finally {
      setLoading(false);
    }
  }

  // Loaded lazily, the first time the section is shown.
  useEffect(() => {
    if (active && !requestedRef.current) {
      requestedRef.current = true;
      void load();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active]);

  const dirty = loaded !== null && (aboutMe !== loaded.aboutMe || responseStyle !== loaded.responseStyle);
  const tooLong = aboutMe.length > PREFERENCE_MAX_LENGTH || responseStyle.length > PREFERENCE_MAX_LENGTH;

  useEffect(() => {
    onDirtyChange(dirty);
  }, [dirty, onDirtyChange]);

  async function save() {
    if (!dirty || tooLong || saving) return;
    setSaving(true);
    setSaveError(null);
    setSavedAt(null);
    try {
      const stored = await savePreferences({ aboutMe, responseStyle });
      setLoaded(stored);
      // Keep what the user sees in sync with what the server actually stored (it may trim).
      setAboutMe(stored.aboutMe);
      setResponseStyle(stored.responseStyle);
      setSavedAt(Date.now());
    } catch (e) {
      if (statusOf(e) === 400 && errorCodeOf(e) === 'TOO_LONG') {
        setSaveError(t('prefs.tooLong', { max: PREFERENCE_MAX_LENGTH }));
      } else {
        setSaveError(t('prefs.saveError', { message: humanError(e, t) }));
      }
    } finally {
      setSaving(false);
    }
  }

  if (loading && !loaded) {
    return <p className="muted settings-panel__status">{t('common.loading')}</p>;
  }

  if (loadError && !loaded) {
    return (
      <div className="settings-error" role="alert">
        <span>
          {t('prefs.loadError')} {loadError}
        </span>
        <button className="dialog-btn" type="button" onClick={() => void load()}>
          {t('prefs.retry')}
        </button>
      </div>
    );
  }

  const fields: {
    id: string;
    label: string;
    hint: string;
    placeholder: string;
    value: string;
    set: (v: string) => void;
  }[] = [
    {
      id: 'prefs-about-me',
      label: t('prefs.aboutMe'),
      hint: t('prefs.aboutMeHint'),
      placeholder: t('prefs.aboutMePlaceholder'),
      value: aboutMe,
      set: setAboutMe,
    },
    {
      id: 'prefs-response-style',
      label: t('prefs.responseStyle'),
      hint: t('prefs.responseStyleHint'),
      placeholder: t('prefs.responseStylePlaceholder'),
      value: responseStyle,
      set: setResponseStyle,
    },
  ];

  return (
    <div className="settings-panel">
      <p className="settings-panel__intro">{t('prefs.intro')}</p>

      {fields.map((f) => {
        const over = f.value.length > PREFERENCE_MAX_LENGTH;
        return (
          <div className="settings-field" key={f.id}>
            <label className="settings-field__label" htmlFor={f.id}>
              {f.label}
            </label>
            <div className="settings-field__hint" id={`${f.id}-hint`}>
              {f.hint}
            </div>
            <textarea
              id={f.id}
              className={`dialog-input settings-textarea ${over ? 'settings-textarea--over' : ''}`}
              value={f.value}
              placeholder={f.placeholder}
              rows={5}
              maxLength={PREFERENCE_MAX_LENGTH}
              aria-describedby={`${f.id}-hint ${f.id}-counter`}
              aria-invalid={over || undefined}
              onChange={(e) => {
                f.set(e.target.value);
                setSavedAt(null);
                setSaveError(null);
              }}
            />
            <div
              className={`settings-field__counter ${over ? 'settings-field__counter--over' : ''}`}
              id={`${f.id}-counter`}
              aria-live="polite"
            >
              {t('prefs.counter', { count: f.value.length, max: PREFERENCE_MAX_LENGTH })}
            </div>
          </div>
        );
      })}

      {saveError && (
        <div className="settings-error" role="alert">
          {saveError}
        </div>
      )}

      <div className="settings-panel__footer">
        {savedAt && !dirty && <span className="settings-panel__saved">{t('prefs.saved')}</span>}
        <button
          className="dialog-btn dialog-btn--primary"
          type="button"
          onClick={() => void save()}
          disabled={!dirty || tooLong || saving}
        >
          {saving ? t('prefs.saving') : t('prefs.save')}
        </button>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------
// USER_MEMORY: добавлено 2026-09-24 (ТЗ-1 этап 2 §1, контракт C-7) — просмотр и очистка фактов,
// которые сервер сам извлекает из чатов, и выключатель памяти целиком.
// ---------------------------------------------------------------------------------------------

function MemorySection({ active }: { active: boolean }) {
  const { t, i18n } = useTranslation();
  const lang = i18n.resolvedLanguage ?? i18n.language;
  const [enabled, setEnabled] = useState(true);
  const [facts, setFacts] = useState<MemoryFact[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [togglePending, setTogglePending] = useState(false);
  const [deletingIds, setDeletingIds] = useState<Set<string>>(() => new Set());
  const [confirmClear, setConfirmClear] = useState(false);
  const [clearing, setClearing] = useState(false);
  const requestedRef = useRef(false);

  async function load() {
    setLoading(true);
    setLoadError(null);
    try {
      const state = await getMemory();
      setEnabled(state.enabled);
      setFacts(state.facts);
    } catch (e) {
      setLoadError(humanError(e, t));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    if (active && !requestedRef.current) {
      requestedRef.current = true;
      void load();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active]);

  async function toggle() {
    if (togglePending) return;
    const next = !enabled;
    setEnabled(next); // optimistic; rolled back below if the server refuses
    setTogglePending(true);
    setActionError(null);
    try {
      await setMemoryEnabled(next);
    } catch (e) {
      setEnabled(!next);
      setActionError(t('memory.actionError', { message: humanError(e, t) }));
    } finally {
      setTogglePending(false);
    }
  }

  async function forget(fact: MemoryFact) {
    if (deletingIds.has(fact.id)) return;
    setDeletingIds((s) => new Set(s).add(fact.id));
    setActionError(null);
    try {
      await deleteMemoryFact(fact.id);
      setFacts((list) => (list ? list.filter((f) => f.id !== fact.id) : list));
    } catch (e) {
      if (statusOf(e) === 404) {
        // Already gone on the server — just drop it from the list.
        setFacts((list) => (list ? list.filter((f) => f.id !== fact.id) : list));
      } else {
        setActionError(t('memory.actionError', { message: humanError(e, t) }));
      }
    } finally {
      setDeletingIds((s) => {
        const n = new Set(s);
        n.delete(fact.id);
        return n;
      });
    }
  }

  async function clearAll() {
    setClearing(true);
    setActionError(null);
    try {
      await clearMemory();
      setFacts([]);
      setConfirmClear(false);
    } catch (e) {
      setConfirmClear(false);
      setActionError(t('memory.actionError', { message: humanError(e, t) }));
    } finally {
      setClearing(false);
    }
  }

  function formatDate(iso: string): string {
    const d = new Date(iso);
    if (!iso || Number.isNaN(d.getTime())) return '';
    try {
      return new Intl.DateTimeFormat(lang, { day: 'numeric', month: 'short', year: 'numeric' }).format(d);
    } catch {
      return d.toLocaleDateString();
    }
  }

  if (loading && facts === null) {
    return <p className="muted settings-panel__status">{t('common.loading')}</p>;
  }

  if (loadError && facts === null) {
    return (
      <div className="settings-error" role="alert">
        <span>
          {t('memory.loadError')} {loadError}
        </span>
        <button className="dialog-btn" type="button" onClick={() => void load()}>
          {t('memory.retry')}
        </button>
      </div>
    );
  }

  const list = facts ?? [];

  return (
    <div className="settings-panel">
      <div className="memory-toggle">
        <div className="memory-toggle__text">
          <div className="settings-field__label" id="memory-toggle-label">
            {t('memory.toggle')}
          </div>
          <p className="settings-panel__intro">{t('memory.intro')}</p>
        </div>
        <button
          type="button"
          role="switch"
          aria-checked={enabled}
          aria-labelledby="memory-toggle-label"
          className={`settings-switch ${enabled ? 'settings-switch--on' : ''}`}
          onClick={() => void toggle()}
          disabled={togglePending}
        >
          <span className="settings-switch__thumb" />
        </button>
      </div>

      <p className="memory-note">{t('memory.incognitoNote')}</p>
      {!enabled && <p className="memory-note memory-note--warn">{t('memory.disabledNote')}</p>}

      {actionError && (
        <div className="settings-error" role="alert">
          {actionError}
        </div>
      )}

      <div className="memory-list__head">
        <span className="settings-modal__label">{t('memory.count', { count: list.length })}</span>
        <button
          type="button"
          className="dialog-btn dialog-btn--danger-outline"
          onClick={() => setConfirmClear(true)}
          disabled={list.length === 0 || clearing}
        >
          {t('memory.clearAll')}
        </button>
      </div>

      {list.length === 0 ? (
        <div className="memory-empty">{t('memory.empty')}</div>
      ) : (
        <ul className="memory-list">
          {list.map((fact) => (
            <li key={fact.id} className="memory-fact">
              <div className="memory-fact__body">
                <div className="memory-fact__text">{fact.text}</div>
                {formatDate(fact.updatedAt || fact.createdAt) && (
                  <div className="memory-fact__date">
                    {t('memory.updatedAt', { date: formatDate(fact.updatedAt || fact.createdAt) })}
                  </div>
                )}
              </div>
              <button
                type="button"
                className="icon-btn memory-fact__delete"
                onClick={() => void forget(fact)}
                disabled={deletingIds.has(fact.id)}
                title={t('memory.forget')}
                aria-label={t('memory.forgetAria', { text: fact.text.slice(0, 80) })}
              >
                <TrashIcon size={15} />
              </button>
            </li>
          ))}
        </ul>
      )}

      {confirmClear && (
        <ConfirmDialog
          title={t('memory.clearTitle')}
          message={t('memory.clearMessage', { count: list.length })}
          confirmLabel={t('memory.clearConfirm')}
          danger
          busy={clearing}
          onConfirm={() => void clearAll()}
          onCancel={() => setConfirmClear(false)}
        />
      )}
    </div>
  );
}

// SETTINGS: добавлено 2026-09-19; разделы «Персонализация» и «Память» — 2026-09-24.
/** Settings modal: theme/language, custom instructions and the user's memory. */
export function SettingsModal({
  theme,
  language,
  onThemeChange,
  onLanguageChange,
  onClose,
  initialSection = 'general',
}: SettingsModalProps) {
  const { t } = useTranslation();
  const [section, setSection] = useState<SettingsSection>(initialSection);
  const [prefsDirty, setPrefsDirty] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);

  // Closing with unsaved personalization edits asks first instead of silently dropping them.
  function requestClose() {
    if (prefsDirty) {
      setConfirmDiscard(true);
      return;
    }
    onClose();
  }

  return (
    <>
      <div className="dialog-overlay" onMouseDown={requestClose}>
        <div
          className="dialog-card settings-modal settings-modal--wide"
          role="dialog"
          aria-modal="true"
          aria-label={t('settings.title')}
          onMouseDown={(e) => e.stopPropagation()}
        >
          <div className="settings-modal__head">
            <div className="dialog-title">{t('settings.title')}</div>
            <button className="icon-btn" onClick={requestClose} aria-label={t('common.close')} type="button">
              <CloseIcon size={18} />
            </button>
          </div>

          <div className="settings-modal__segmented settings-tabs" role="tablist">
            {SECTIONS.map((s) => (
              <button
                key={s.value}
                role="tab"
                aria-selected={section === s.value}
                className={`settings-seg ${section === s.value ? 'settings-seg--active' : ''}`}
                onClick={() => setSection(s.value)}
                type="button"
              >
                {t(s.key)}
              </button>
            ))}
          </div>

          <div className="settings-modal__body">
            <div hidden={section !== 'general'}>
              <div className="settings-panel">
                <div className="settings-modal__section">
                  <div className="settings-modal__label">{t('settings.theme')}</div>
                  <div className="settings-modal__segmented">
                    {THEMES.map((th) => (
                      <button
                        key={th.value}
                        className={`settings-seg ${theme === th.value ? 'settings-seg--active' : ''}`}
                        onClick={() => onThemeChange(th.value)}
                        type="button"
                      >
                        {t(th.key)}
                      </button>
                    ))}
                  </div>
                </div>

                <div className="settings-modal__section">
                  <div className="settings-modal__label">{t('settings.language')}</div>
                  <div className="settings-modal__segmented">
                    {LANGS.map((l) => (
                      <button
                        key={l.value}
                        className={`settings-seg ${language === l.value ? 'settings-seg--active' : ''}`}
                        onClick={() => onLanguageChange(l.value)}
                        type="button"
                      >
                        {t(l.key)}
                      </button>
                    ))}
                  </div>
                </div>
              </div>
            </div>

            {/* Sections stay mounted (only hidden) so switching tabs never drops typed text. */}
            <div hidden={section !== 'personalization'}>
              <PersonalizationSection active={section === 'personalization'} onDirtyChange={setPrefsDirty} />
            </div>

            <div hidden={section !== 'memory'}>
              <MemorySection active={section === 'memory'} />
            </div>
          </div>
        </div>
      </div>

      {confirmDiscard && (
        <ConfirmDialog
          title={t('prefs.discardTitle')}
          message={t('prefs.discardMessage')}
          confirmLabel={t('prefs.discard')}
          danger
          onConfirm={() => {
            setConfirmDiscard(false);
            onClose();
          }}
          onCancel={() => setConfirmDiscard(false)}
        />
      )}
    </>
  );
}
