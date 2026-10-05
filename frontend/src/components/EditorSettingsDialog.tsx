import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { getStoredTheme, setTheme, type Theme } from '../theme';
import {
  HOTKEY_ACTIONS,
  comboFromEvent,
  resetEditorSettings,
  updateEditorSettings,
  useEditorSettings,
  type HotkeyAction,
} from '../utils/editorSettings';

// EDITOR_SETTINGS: добавлено 2026-10-05 — экран настроек редактора: тема, размер шрифта, размер
// табов (2/4 пробела) и переназначение горячих клавиш.
interface EditorSettingsDialogProps {
  open: boolean;
  onClose: () => void;
}

export function EditorSettingsDialog({ open, onClose }: EditorSettingsDialogProps) {
  const { t } = useTranslation();
  const settings = useEditorSettings();
  const [theme, setThemeState] = useState<Theme>(getStoredTheme);
  const [recording, setRecording] = useState<HotkeyAction | null>(null);

  // While recording, capture the next key chord as the new binding.
  useEffect(() => {
    if (!recording) return;
    function onKeyDown(e: KeyboardEvent) {
      e.preventDefault();
      e.stopPropagation();
      if (e.key === 'Escape') {
        setRecording(null);
        return;
      }
      const combo = comboFromEvent(e);
      if (!combo) return; // modifier-only: keep waiting
      updateEditorSettings({ hotkeys: { [recording as HotkeyAction]: combo } });
      setRecording(null);
    }
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [recording]);

  useEffect(() => {
    if (open) setThemeState(getStoredTheme());
  }, [open]);

  if (!open) return null;

  function changeTheme(next: Theme) {
    setThemeState(next);
    setTheme(next);
  }

  return (
    <div className="dialog-overlay dialog-overlay--compact" onMouseDown={onClose}>
      <div className="dialog-card settings-dialog" role="dialog" aria-modal="true" onMouseDown={(e) => e.stopPropagation()}>
        <div className="dialog-title">{t('settings.editorTitle')}</div>

        <div className="settings-dialog__section">
          <label className="settings-dialog__row">
            <span>{t('settings.theme')}</span>
            <select className="dialog-input settings-dialog__control" value={theme} onChange={(e) => changeTheme(e.target.value as Theme)}>
              <option value="light">{t('settings.themeLight')}</option>
              <option value="dark">{t('settings.themeDark')}</option>
              <option value="system">{t('settings.themeSystem')}</option>
            </select>
          </label>

          <label className="settings-dialog__row">
            <span>{t('settings.fontSize')}</span>
            <span className="settings-dialog__inline">
              <input
                type="range"
                min={10}
                max={24}
                value={settings.fontSize}
                onChange={(e) => updateEditorSettings({ fontSize: Number(e.target.value) })}
              />
              <span className="settings-dialog__value">{settings.fontSize}px</span>
            </span>
          </label>

          <label className="settings-dialog__row">
            <span>{t('settings.tabSize')}</span>
            <span className="settings-dialog__inline">
              <button
                className={`settings-dialog__tab ${settings.tabSize === 2 ? 'settings-dialog__tab--on' : ''}`}
                onClick={() => updateEditorSettings({ tabSize: 2 })}
                type="button"
              >
                2
              </button>
              <button
                className={`settings-dialog__tab ${settings.tabSize === 4 ? 'settings-dialog__tab--on' : ''}`}
                onClick={() => updateEditorSettings({ tabSize: 4 })}
                type="button"
              >
                4
              </button>
            </span>
          </label>
        </div>

        <div className="settings-dialog__section">
          <div className="settings-dialog__heading">{t('settings.hotkeys')}</div>
          {HOTKEY_ACTIONS.map((action) => (
            <div className="settings-dialog__row" key={action}>
              <span>{t(`settings.action_${action}`)}</span>
              <button
                className={`settings-dialog__hotkey ${recording === action ? 'settings-dialog__hotkey--recording' : ''}`}
                onClick={() => setRecording(action)}
                type="button"
              >
                {recording === action ? t('settings.pressKeys') : settings.hotkeys[action]}
              </button>
            </div>
          ))}
        </div>

        <div className="dialog-actions">
          <button
            className="dialog-btn"
            onClick={() => {
              resetEditorSettings();
              setRecording(null);
            }}
            type="button"
          >
            {t('settings.reset')}
          </button>
          <button className="dialog-btn dialog-btn--primary" onClick={onClose} type="button">
            {t('common.close')}
          </button>
        </div>
      </div>
    </div>
  );
}
