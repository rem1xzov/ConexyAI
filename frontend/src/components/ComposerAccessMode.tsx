import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { CheckIcon, ChevronDownIcon, ShieldIcon } from './Icons';

// VISUAL_DESKTOP: пилюля режима доступа в композере окна — как «Запись в рабочей области» в
// референсе DeepSeek Harness. Два реальных режима: работа в папке с подтверждениями (по умолчанию)
// и полный доступ к ПК (как /control on). Значение хранится на ПК и приходит в /device/status.
interface AccessModeChipProps {
  fullControl: boolean;
  onChange: (fullControl: boolean) => void;
  disabled?: boolean;
}

interface MenuPosition {
  left: number;
  top: number;
  width: number;
}

const TRIGGER_HEIGHT = 30;
const MENU_WIDTH = 288;
const MENU_MAX_HEIGHT = 260;

const triggerStyle: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 6,
  height: TRIGGER_HEIGHT,
  padding: '0 10px',
  borderRadius: 999,
  border: '1px solid rgba(255, 255, 255, 0.1)',
  background: 'transparent',
  color: 'var(--muted)',
  fontFamily: 'inherit',
  fontSize: 12.5,
  lineHeight: 1,
  whiteSpace: 'nowrap',
  cursor: 'pointer',
  transition: 'background 120ms ease, border-color 120ms ease, color 120ms ease',
};

const menuStyle: CSSProperties = {
  position: 'fixed',
  zIndex: 1000,
  boxSizing: 'border-box',
  display: 'flex',
  flexDirection: 'column',
  padding: 6,
  overflowY: 'auto',
  border: '1px solid rgba(255, 255, 255, 0.09)',
  borderRadius: 14,
  background: '#17171a',
  boxShadow: '0 16px 40px rgba(0, 0, 0, 0.6)',
};

function itemStyle(highlighted: boolean): CSSProperties {
  return {
    display: 'flex',
    alignItems: 'flex-start',
    gap: 8,
    width: '100%',
    padding: '9px 10px',
    border: 'none',
    borderRadius: 9,
    background: highlighted ? 'rgba(255, 255, 255, 0.06)' : 'transparent',
    color: 'var(--text)',
    fontFamily: 'inherit',
    textAlign: 'left',
    cursor: 'pointer',
  };
}

export function AccessModeChip({ fullControl, onChange, disabled }: AccessModeChipProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<MenuPosition | null>(null);
  const [highlighted, setHighlighted] = useState(fullControl ? 1 : 0);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const label = fullControl ? t('device.accessFull') : t('device.accessWrite');

  const place = () => {
    const rect = triggerRef.current?.getBoundingClientRect();
    if (!rect) return;
    const below = window.innerHeight - rect.bottom - 8;
    const height = Math.min(MENU_MAX_HEIGHT, 160);
    const top = below >= height ? rect.bottom + 6 : Math.max(8, rect.top - height - 6);
    const left = Math.min(rect.left, window.innerWidth - MENU_WIDTH - 8);
    setPos({ left: Math.max(8, left), top, width: MENU_WIDTH });
  };

  useLayoutEffect(() => {
    if (!open) return;
    place();
    const onScroll = () => place();
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onScroll);
    return () => {
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onScroll);
    };
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

  const choose = (full: boolean) => {
    setOpen(false);
    if (full !== fullControl) onChange(full);
  };

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        className="composer-access"
        style={{ ...triggerStyle, opacity: disabled ? 0.55 : 1, cursor: disabled ? 'default' : 'pointer' }}
        onClick={() => {
          if (!disabled) setOpen((value) => !value);
        }}
        onMouseEnter={() => setHighlighted(fullControl ? 1 : 0)}
        aria-haspopup="listbox"
        aria-expanded={open}
        disabled={disabled}
        title={fullControl ? t('device.accessFullHint') : t('device.accessWriteHint')}
      >
        <ShieldIcon size={14} />
        <span>{label}</span>
        <span style={{ display: 'inline-flex', color: 'var(--muted)', transform: open ? 'rotate(180deg)' : undefined }}>
          <ChevronDownIcon size={12} />
        </span>
      </button>
      {open &&
        createPortal(
          <div
            ref={menuRef}
            role="listbox"
            aria-label={t('device.accessMode')}
            style={{ ...menuStyle, ...(pos ?? { left: 0, top: 0, width: MENU_WIDTH, visibility: 'hidden' as const }) }}
          >
            {[
              { full: false, title: t('device.accessWrite'), hint: t('device.accessWriteHint') },
              { full: true, title: t('device.accessFull'), hint: t('device.accessFullHint') },
            ].map((option, index) => {
              const selected = option.full === fullControl;
              return (
                <button
                  key={option.title}
                  type="button"
                  role="option"
                  aria-selected={selected}
                  style={itemStyle(highlighted === index)}
                  onMouseEnter={() => setHighlighted(index)}
                  onClick={() => choose(option.full)}
                >
                  <span style={{ display: 'inline-flex', flex: 'none', paddingTop: 2, color: 'var(--muted)' }}>
                    <ShieldIcon size={15} />
                  </span>
                  <span style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 0 }}>
                    <span style={{ fontSize: 13.5 }}>{option.title}</span>
                    <span style={{ fontSize: 12, color: 'var(--muted)', lineHeight: 1.35 }}>{option.hint}</span>
                  </span>
                  {selected && (
                    <span style={{ marginLeft: 'auto', display: 'inline-flex', color: 'var(--accent)', paddingTop: 1 }}>
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
