import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { CheckIcon, ChevronDownIcon, MonitorIcon, TerminalIcon } from './Icons';

// VISUAL_DESKTOP: выбор, что открывать при обычном запуске conexy.exe — визуальное окно или
// консоль. Живёт в шапке окна, чтобы пользователь мог переключить режим прямо из визуального
// десктопа, не возвращаясь в терминал и не вспоминая консольные команды.
interface DesktopDefaultMenuProps {
  visualDefault: boolean;
  onChange: (visual: boolean) => void;
  disabled?: boolean;
}

interface MenuPosition {
  left: number;
  top: number;
  width: number;
}

const WIDTH = 300;

const triggerStyle: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 6,
  flex: '0 0 auto',
  height: 34,
  padding: '0 12px',
  borderRadius: 999,
  border: '1px solid var(--border)',
  background: 'transparent',
  color: 'var(--muted)',
  fontFamily: 'inherit',
  fontSize: 12.5,
  lineHeight: 1,
  whiteSpace: 'nowrap',
  cursor: 'pointer',
};

const menuStyle: CSSProperties = {
  position: 'fixed',
  zIndex: 1200,
  boxSizing: 'border-box',
  display: 'flex',
  flexDirection: 'column',
  padding: 6,
  border: '1px solid rgba(255, 255, 255, 0.09)',
  borderRadius: 14,
  background: '#17171a',
  boxShadow: '0 16px 40px rgba(0, 0, 0, 0.6)',
};

function itemStyle(active: boolean): CSSProperties {
  return {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    width: '100%',
    height: 38,
    padding: '0 10px',
    border: 'none',
    borderRadius: 9,
    background: active ? 'rgba(255, 255, 255, 0.06)' : 'transparent',
    color: 'var(--text)',
    fontFamily: 'inherit',
    fontSize: 13.5,
    textAlign: 'left',
    cursor: 'pointer',
  };
}

export function DesktopDefaultMenu({ visualDefault, onChange, disabled }: DesktopDefaultMenuProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<MenuPosition | null>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    if (!open) return;
    const rect = triggerRef.current?.getBoundingClientRect();
    if (!rect) return;
    const left = Math.max(8, Math.min(rect.left, window.innerWidth - WIDTH - 8));
    const top = Math.min(rect.bottom + 6, window.innerHeight - 140);
    setPos({ left, top, width: WIDTH });
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onPointer = (event: PointerEvent) => {
      const target = event.target as Node;
      if (triggerRef.current?.contains(target) || menuRef.current?.contains(target)) return;
      setOpen(false);
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    window.addEventListener('pointerdown', onPointer, true);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('pointerdown', onPointer, true);
      window.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const choose = (visual: boolean) => {
    setOpen(false);
    if (visual !== visualDefault) onChange(visual);
  };

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        className="desktop-default"
        style={{ ...triggerStyle, opacity: disabled ? 0.55 : 1 }}
        onClick={() => {
          if (!disabled) setOpen((value) => !value);
        }}
        disabled={disabled}
        title={t('visualDesktop.defaultHint')}
      >
        <MonitorIcon size={14} />
        <span>{visualDefault ? t('visualDesktop.defaultWindow') : t('visualDesktop.defaultConsole')}</span>
        <span style={{ display: 'inline-flex', color: 'var(--muted)', transform: open ? 'rotate(180deg)' : undefined }}>
          <ChevronDownIcon size={12} />
        </span>
      </button>
      {open &&
        createPortal(
          <div ref={menuRef} role="menu" aria-label={t('visualDesktop.defaultHint')} style={{ ...menuStyle, ...(pos ?? { left: 0, top: 0, width: WIDTH, visibility: 'hidden' as const }) }}>
            <div style={{ padding: '4px 10px 8px', fontSize: 11.5, color: 'var(--muted)', lineHeight: 1.4 }}>
              {t('visualDesktop.defaultHint')}
            </div>
            {[
              { visual: true, label: t('visualDesktop.defaultWindow'), Icon: MonitorIcon },
              { visual: false, label: t('visualDesktop.defaultConsole'), Icon: TerminalIcon },
            ].map((option) => {
              const active = option.visual === visualDefault;
              return (
                <button key={String(option.visual)} type="button" role="menuitemradio" aria-checked={active} style={itemStyle(active)} onClick={() => choose(option.visual)}>
                  <span style={{ display: 'inline-flex', color: 'var(--muted)' }}>
                    <option.Icon size={15} />
                  </span>
                  <span>{option.label}</span>
                  {active && (
                    <span style={{ marginLeft: 'auto', display: 'inline-flex', color: 'var(--accent)' }}>
                      <CheckIcon size={14} />
                    </span>
                  )}
                </button>
              );
            })}
          </div>,
          document.body,
        )}
    </>
  );
}
