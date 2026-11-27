import {
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent as ReactKeyboardEvent,
} from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import type { DeviceStatus } from '../api/deviceApi';
import { CheckIcon, ChevronDownIcon, FolderIcon, PlusIcon } from './Icons';

// VISUAL_DESKTOP: визуальный десктоп (`/desktop`) — это тот же веб-клиент, но агент в нём всегда
// работает на ПК пользователя. Поэтому выбора «Песочница / Мой ПК» там нет: вместо него в меню
// моделей показывается рабочая область — папка на ПК, в которой агент выполняет команды и правит
// файлы. Разметка переиспользует классы `.device-target*` для заголовка и строки статуса.
//
// WORKSPACE_MENU: добавлено 2026-11-27 — вместо нативного `<select>` теперь один общий кастомный
// дропдаун для обоих мест (поле в меню моделей и чип в композере). Нативный список рисовала ОС:
// белая панель выпадала поверх тёмного листа моделей, а в композере открывался чужой системный
// дропдаун. Меню рендерится порталом в `document.body` и позиционируется `position: fixed` по
// прямоугольнику триггера — поэтому его не режет ни лист моделей, ни `overflow` композера.
interface WorkspacePickerProps {
  status: DeviceStatus | null;
  /** Persisted selection; when it is missing or stale the first project is shown. */
  projectId: string | null;
  onProjectChange: (projectId: string) => void;
  /**
   * Real "add workspace": asks the connected PC to open its native folder picker and re-register the
   * project list (`POST /device/workspace`); the new folder shows up after `device.refresh()`.
   */
  onAddWorkspace?: () => void;
  /** The add request is in flight — the last menu row shows a spinner and is disabled. */
  adding?: boolean;
  disabled?: boolean;
}

type Variant = 'section' | 'chip';

interface MenuPosition {
  left: number;
  top: number;
  width: number;
  maxHeight: number;
}

/** Gap between the menu and the trigger. */
const MENU_TRIGGER_GAP = 6;
/** Minimum distance the menu keeps from the viewport edges. */
const MENU_VIEWPORT_GAP = 8;
/** The menu can grow to the trigger width, but never wider than this (compact pill popup in the reference). */
const MENU_MAX_WIDTH = 320;
/** Even a narrow chip gets a menu wide enough for «Добавить рабочее пространство…». */
const MENU_MIN_WIDTH = 272;
const MENU_MAX_HEIGHT = 360;

const headingStyle: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 6,
};

const statusRowStyle: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 5,
  minWidth: 0,
  padding: '0 10px 6px',
  fontSize: 12,
};

const hintStyle: CSSProperties = {
  display: 'block',
  padding: '0 10px 8px',
  color: 'var(--muted)',
  fontSize: 11.5,
  lineHeight: 1.35,
};

const commandStyle: CSSProperties = {
  display: 'inline-block',
  marginTop: 3,
  padding: '1px 6px',
  borderRadius: 5,
  background: 'var(--surface-2)',
  color: 'var(--text)',
  fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
  fontSize: 11,
  userSelect: 'all',
};

// ---- shared trigger / menu styles (inline, like the rest of this component; no new CSS file) ----

const sectionTriggerStyle: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 8,
  width: 'calc(100% - 20px)',
  height: 40,
  margin: '2px 10px 6px',
  padding: '0 12px',
  borderRadius: 10,
  background: 'var(--surface-2)',
  color: 'var(--text)',
  fontFamily: 'inherit',
  fontSize: 13,
  textAlign: 'left',
  transition: 'border-color 120ms ease, background 120ms ease',
};

const chipTriggerStyle: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 6,
  flex: '0 0 auto',
  minWidth: 0,
  maxWidth: 180,
  height: 33,
  padding: '0 10px',
  borderRadius: 999,
  background: 'var(--surface-2)',
  color: 'var(--text)',
  fontFamily: 'inherit',
  fontSize: 12,
  lineHeight: 1,
  whiteSpace: 'nowrap',
  transition: 'border-color 120ms ease, background 120ms ease',
};

const triggerIconStyle: CSSProperties = {
  display: 'inline-flex',
  flex: 'none',
  color: 'var(--muted)',
};

const triggerLabelStyle: CSSProperties = {
  flex: '0 1 auto',
  minWidth: 0,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const triggerSpacerStyle: CSSProperties = {
  flex: '1 1 auto',
  minWidth: 0,
};

const chevronStyle: CSSProperties = {
  display: 'inline-flex',
  flex: 'none',
  color: 'var(--muted)',
  transition: 'transform 140ms ease',
};

const menuStyle: CSSProperties = {
  position: 'fixed',
  zIndex: 1000,
  boxSizing: 'border-box',
  display: 'flex',
  flexDirection: 'column',
  padding: 6,
  overflowY: 'auto',
  overscrollBehavior: 'contain',
  border: '1px solid var(--border)',
  borderRadius: 12,
  // Reference: `--surface-solid` dark popup; the literal is only a fallback for an unthemed page.
  background: 'var(--surface-solid, #17121c)',
  boxShadow: '0 12px 32px rgba(0, 0, 0, 0.55)',
};

function itemStyle(highlighted: boolean): CSSProperties {
  return {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    flex: 'none',
    width: '100%',
    height: 36,
    padding: '0 10px',
    border: 'none',
    borderRadius: 8,
    background: highlighted ? 'var(--bg-hover, rgba(255, 255, 255, 0.06))' : 'transparent',
    color: 'var(--text)',
    fontFamily: 'inherit',
    fontSize: 13,
    textAlign: 'left',
    whiteSpace: 'nowrap',
    cursor: 'pointer',
    transition: 'background 100ms ease',
  };
}

function addItemStyle(highlighted: boolean, disabled: boolean): CSSProperties {
  return {
    ...itemStyle(highlighted),
    color: 'var(--muted)',
    cursor: disabled ? 'default' : 'pointer',
    opacity: disabled ? 0.7 : 1,
  };
}

const itemIconStyle: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  flex: 'none',
  width: 16,
  color: 'var(--muted)',
};

const itemLabelStyle: CSSProperties = {
  flex: '1 1 auto',
  minWidth: 0,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const checkStyle: CSSProperties = {
  display: 'inline-flex',
  flex: 'none',
  color: 'var(--accent)',
};

// Reference divider is white 8% on dark; `--border` is the app's token for the same hairline
// (rgba(255, 255, 255, .09) in the dark theme, a visible tint in the light/incognito themes).
const dividerStyle: CSSProperties = {
  flex: 'none',
  height: 1,
  margin: '6px 4px',
  background: 'var(--border)',
};

/**
 * Один общий кастомный дропдаун рабочей области: полноширинное поле в меню моделей (`section`) и
 * компактная пилюля в композере (`chip`). Меню порталится в `document.body`, поэтому не зависит от
 * `overflow`/`transform` родителя (лист моделей прокручивается и анимируется) и всегда видно поверх.
 */
function WorkspaceDropdown({
  variant,
  status,
  projectId,
  onProjectChange,
  onAddWorkspace,
  adding = false,
  disabled = false,
}: WorkspacePickerProps & { variant: Variant }) {
  const { t } = useTranslation();
  const projects = status?.projects ?? [];
  const current = projects.some((p) => p.id === projectId) ? projectId : (projects[0]?.id ?? null);
  const currentName = projects.find((p) => p.id === current)?.name ?? null;
  // Пункт «Добавить рабочее пространство…» — последняя строка меню и доступен всегда, даже когда на
  // ПК ещё нет ни одной папки: тогда его наличие и есть способ добавить первую.
  const itemCount = projects.length + 1;

  const [open, setOpen] = useState(false);
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);
  const [activeIndex, setActiveIndex] = useState(0);
  const [pos, setPos] = useState<MenuPosition | null>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const wasAddingRef = useRef(false);
  // Клавиатурная навигация двигает подсветку без прокрутки списка — доводим выбранную строку в вид
  // (при наведении мышью не трогаем прокрутку, иначе меню «прыгает» под курсором).
  const keyboardNavRef = useRef(false);
  const menuId = useId();
  const optionId = (index: number) => `${menuId}-option-${index}`;

  // Пока статус ПК не загружен, меню открывать нечего: не мигаем пустым списком при загрузке.
  const triggerDisabled = disabled || !status;

  const close = useCallback((restoreFocus: boolean) => {
    setOpen(false);
    setPos(null);
    if (restoreFocus) triggerRef.current?.focus({ preventScroll: true });
  }, []);

  /**
   * Позиция меню по прямоугольнику триггера: открываем вниз, если снизу хватает места, иначе вверх.
   * Ширина — от ширины триггера до 320px; высота ограничена свободным местом, при переполнении
   * список прокручивается (сам лист моделей при этом не сдвигается — меню в портале).
   */
  const updatePosition = useCallback(() => {
    const trigger = triggerRef.current;
    const menu = menuRef.current;
    if (!trigger || !menu) return;
    const rect = trigger.getBoundingClientRect();
    const viewportW = window.innerWidth;
    const viewportH = window.innerHeight;
    const width = Math.max(MENU_MIN_WIDTH, Math.min(MENU_MAX_WIDTH, Math.round(rect.width)));

    // `scrollHeight` — высота содержимого без учёта текущего `max-height`, поэтому переворот вверх
    // считается честно с первого прохода.
    const contentHeight = menu.scrollHeight;
    const belowSpace = Math.max(0, viewportH - rect.bottom - MENU_TRIGGER_GAP - MENU_VIEWPORT_GAP);
    const aboveSpace = Math.max(0, rect.top - MENU_TRIGGER_GAP - MENU_VIEWPORT_GAP);
    const openUp = contentHeight > belowSpace && aboveSpace > belowSpace;
    const available = openUp ? aboveSpace : belowSpace;
    const maxHeight = Math.max(120, Math.min(MENU_MAX_HEIGHT, available));
    const height = Math.min(contentHeight, maxHeight);

    const left = Math.min(
      Math.max(MENU_VIEWPORT_GAP, Math.round(rect.left)),
      Math.max(MENU_VIEWPORT_GAP, viewportW - MENU_VIEWPORT_GAP - width),
    );
    const top = openUp
      ? Math.max(MENU_VIEWPORT_GAP, Math.round(rect.top) - MENU_TRIGGER_GAP - height)
      : Math.min(
          Math.round(rect.bottom) + MENU_TRIGGER_GAP,
          Math.max(MENU_VIEWPORT_GAP, viewportH - MENU_VIEWPORT_GAP - height),
        );

    setPos((prev) =>
      prev && prev.left === left && prev.top === top && prev.width === width && prev.maxHeight === maxHeight
        ? prev
        : { left, top, width, maxHeight },
    );
  }, []);

  // Первый замер — до отрисовки (useLayoutEffect), чтобы меню не мигало в левом верхнем углу.
  useLayoutEffect(() => {
    if (!open) return;
    updatePosition();
    let frame = 0;
    const schedule = () => {
      if (frame) return;
      frame = window.requestAnimationFrame(() => {
        frame = 0;
        updatePosition();
      });
    };
    window.addEventListener('resize', schedule);
    // `scroll` не всплывает: capture-слушатель ловит и прокрутку тела листа моделей, и ленту чата.
    window.addEventListener('scroll', schedule, true);
    return () => {
      window.removeEventListener('resize', schedule);
      window.removeEventListener('scroll', schedule, true);
      if (frame) window.cancelAnimationFrame(frame);
    };
  }, [open, updatePosition]);

  // Содержимое могло измениться, пока меню открыто (пришёл новый проект, включился спиннер) —
  // высота другая, пересчитываем и положение.
  useLayoutEffect(() => {
    if (open) updatePosition();
  }, [open, updatePosition, projects.length, adding, current, status?.online]);

  // Закрытие: клик мимо меню/триггера и Escape. Escape перехватываем в capture-фазе и глушим, иначе
  // он закроет ещё и лист моделей, внутри которого меню открыто.
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: PointerEvent) => {
      const target = event.target as Node | null;
      if (!target) return;
      if (triggerRef.current?.contains(target) || menuRef.current?.contains(target)) return;
      close(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.stopPropagation();
      close(true);
    };
    document.addEventListener('pointerdown', onPointerDown, true);
    document.addEventListener('keydown', onKeyDown, true);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown, true);
      document.removeEventListener('keydown', onKeyDown, true);
    };
  }, [open, close]);

  // Агент запустился — переключение и добавление запрещены, открытое меню закрываем.
  useEffect(() => {
    if (disabled && open) close(false);
  }, [disabled, open, close]);

  // Добавление закончилось (успехом или ошибкой) — меню со спиннером больше не нужно, закрываем и
  // возвращаем фокус на триггер. Пока запрос идёт, меню остаётся открытым, чтобы строку
  // «Добавляю…» со спиннером было видно.
  useEffect(() => {
    if (wasAddingRef.current && !adding && open) close(true);
    wasAddingRef.current = adding;
  }, [adding, open, close]);

  // Клавиатурная подсветка может уехать за пределы прокручиваемого меню — доводим строку в вид.
  useEffect(() => {
    if (!open || !keyboardNavRef.current) return;
    document.getElementById(`${menuId}-option-${activeIndex}`)?.scrollIntoView({ block: 'nearest' });
  }, [open, activeIndex, projects.length, menuId]);

  function toggleOpen() {
    if (triggerDisabled) return;
    if (open) {
      close(true);
      return;
    }
    // Подсветка стартует с текущей папки, как у нативного списка.
    const index = projects.findIndex((p) => p.id === current);
    keyboardNavRef.current = false;
    setActiveIndex(index >= 0 ? index : 0);
    setOpen(true);
  }

  function selectProject(id: string) {
    onProjectChange(id);
    close(true);
  }

  function startAdd() {
    if (adding) return;
    // Меню НЕ закрываем: в последней строке включается спиннер «Добавляю…», и видно, что запрос
    // ушёл. Закроется оно само, когда добавление завершится (эффект выше), либо по клику/Escape.
    onAddWorkspace?.();
  }

  function onTriggerKeyDown(event: ReactKeyboardEvent<HTMLButtonElement>) {
    if (triggerDisabled) return;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      keyboardNavRef.current = true;
      if (!open) {
        const index = projects.findIndex((p) => p.id === current);
        setActiveIndex(event.key === 'ArrowUp' ? itemCount - 1 : Math.max(0, index));
        setOpen(true);
        return;
      }
      const delta = event.key === 'ArrowDown' ? 1 : -1;
      setActiveIndex((index) => (index + delta + itemCount) % itemCount);
      return;
    }
    if ((event.key === 'Enter' || event.key === ' ') && open) {
      // Enter/Space по умолчанию перещёлкнули бы триггер — вместо этого берём подсвеченную строку.
      event.preventDefault();
      if (activeIndex >= projects.length) startAdd();
      else if (projects[activeIndex]) selectProject(projects[activeIndex].id);
      return;
    }
    if (event.key === 'Tab' && open) close(false);
  }

  const isSection = variant === 'section';
  const label = currentName ?? t('device.workspace');
  const highlightedBorder =
    !triggerDisabled && (hovered || focused || open) ? 'var(--accent)' : 'var(--border)';

  const trigger = (
    <button
      ref={triggerRef}
      type="button"
      className={variant === 'section' ? 'device-target__trigger' : 'workspace-chip__trigger'}
      onClick={toggleOpen}
      onKeyDown={onTriggerKeyDown}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      disabled={triggerDisabled}
      aria-haspopup="listbox"
      aria-expanded={open}
      aria-controls={open ? menuId : undefined}
      aria-activedescendant={open ? optionId(activeIndex) : undefined}
      title={isSection ? t('device.workspaceHint') : (currentName ?? t('device.workspaceHint'))}
      style={{
        ...(isSection ? sectionTriggerStyle : chipTriggerStyle),
        border: `1px solid ${highlightedBorder}`,
        cursor: triggerDisabled ? 'default' : 'pointer',
        opacity: triggerDisabled ? 0.55 : 1,
      }}
    >
      <span style={triggerIconStyle} aria-hidden="true">
        <FolderIcon size={isSection ? 15 : 14} />
      </span>
      <span style={triggerLabelStyle}>{label}</span>
      {isSection && <span style={triggerSpacerStyle} aria-hidden="true" />}
      <span style={{ ...chevronStyle, transform: open ? 'rotate(180deg)' : undefined }} aria-hidden="true">
        <ChevronDownIcon size={isSection ? 14 : 12} />
      </span>
    </button>
  );

  const menu = open
    ? createPortal(
        <div
          ref={menuRef}
          id={menuId}
          role="listbox"
          aria-label={t('device.workspaceSelect')}
          style={{
            ...menuStyle,
            ...(pos
              ? { left: pos.left, top: pos.top, width: pos.width, maxHeight: pos.maxHeight }
              : // Первый кадр до замера: невидимо, чтобы не мигало в углу.
                { left: 0, top: 0, maxHeight: MENU_MAX_HEIGHT, visibility: 'hidden' }),
          }}
        >
          {projects.map((project, index) => {
            const selected = project.id === current;
            return (
              <button
                key={project.id}
                id={optionId(index)}
                type="button"
                role="option"
                aria-selected={selected}
                disabled={disabled}
                title={project.name}
                onMouseEnter={() => {
                  keyboardNavRef.current = false;
                  setActiveIndex(index);
                }}
                onClick={() => selectProject(project.id)}
                style={{
                  ...itemStyle(activeIndex === index),
                  ...(disabled ? { cursor: 'default', opacity: 0.6 } : null),
                }}
              >
                <span style={itemIconStyle} aria-hidden="true">
                  <FolderIcon size={15} />
                </span>
                <span style={itemLabelStyle}>{project.name}</span>
                {selected && (
                  <span style={checkStyle} aria-hidden="true">
                    <CheckIcon size={14} />
                  </span>
                )}
              </button>
            );
          })}

          {projects.length > 0 && <span style={dividerStyle} role="presentation" />}

          <button
            id={optionId(projects.length)}
            type="button"
            role="option"
            aria-selected={false}
            aria-disabled={adding}
            disabled={adding}
            onMouseEnter={() => {
              keyboardNavRef.current = false;
              setActiveIndex(projects.length);
            }}
            onClick={startAdd}
            style={addItemStyle(activeIndex === projects.length, adding)}
          >
            <span style={itemIconStyle} aria-hidden="true">
              {adding ? <span className="action-line__spinner" /> : <PlusIcon size={15} />}
            </span>
            <span style={itemLabelStyle}>{adding ? t('device.workspaceAdding') : t('device.workspaceAdd')}</span>
          </button>
        </div>,
        document.body,
      )
    : null;

  return (
    <>
      {trigger}
      {menu}
    </>
  );
}

/** Current workspace in the model sheet: heading, PC status row, custom dropdown field and a hint. */
export function WorkspaceSection({ status, projectId, onProjectChange, onAddWorkspace, adding, disabled }: WorkspacePickerProps) {
  const { t } = useTranslation();
  const projects = status?.projects ?? [];

  return (
    <div className="device-target">
      <span className="device-target__heading" style={headingStyle}>
        <FolderIcon size={14} />
        {t('device.workspace')}
      </span>
      {/* PC status stays visible (dot + machine name) — only the target radios are gone. */}
      <span className="device-target__status" style={statusRowStyle}>
        <span className={`device-target__dot ${status?.online ? 'device-target__dot--online' : ''}`} aria-hidden="true" />
        <span className="device-target__name">{status?.name || t('device.unknownPc')}</span>
        <span className={status?.online ? '' : 'device-target__offline'}>
          · {status?.online ? t('device.online') : t('device.offline')}
        </span>
      </span>

      <WorkspaceDropdown
        variant="section"
        status={status}
        projectId={projectId}
        onProjectChange={onProjectChange}
        onAddWorkspace={onAddWorkspace}
        adding={adding}
        disabled={disabled}
      />

      {projects.length > 0 ? (
        <span style={hintStyle}>{t('device.workspaceHint')}</span>
      ) : status ? (
        // Статус уже загружен и папок действительно нет — подсказываем, как добавить папку.
        <span style={hintStyle}>
          {t('device.workspaceEmpty')}
          <br />
          <code style={commandStyle}>{t('device.workspaceAddHint')}</code>
        </span>
      ) : null}
    </div>
  );
}

/**
 * Compact folder chip for the composer (visual desktop only): the current workspace is visible and
 * switchable without opening the model sheet, and the same menu holds the real "add workspace" action.
 * The chip also stays visible when the PC reports no folders yet, so the first one can be added here.
 */
export function WorkspaceChip(props: WorkspacePickerProps) {
  // Пока статус ПК не загружен — ничего не показываем, чтобы не мигать пустой пилюлей.
  if (!props.status) return null;
  return <WorkspaceDropdown variant="chip" {...props} />;
}
