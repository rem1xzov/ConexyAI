import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';

function DialogShell({
  onCancel,
  labelledBy,
  children,
}: {
  onCancel: () => void;
  labelledBy?: string;
  children: ReactNode;
}) {
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onCancel();
    }
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onCancel]);

  // CONFIRM_DIALOGS: добавлено 2026-09-24 — диалог рендерится в body: у боковой панели и других
  // контейнеров есть backdrop-filter, а он делает их containing block для position: fixed, и
  // оверлей оказывался зажат внутри сайдбара вместо того, чтобы закрыть весь экран.
  return createPortal(
    // DIALOG_MOBILE: `dialog-overlay--compact` marks the small confirm/prompt dialogs. On a phone a
    // full-screen sheet is right for the large modals but leaves a 360px-wide confirmation glued to
    // the status bar, so the two cases are told apart in CSS by this class alone.
    <div className="dialog-overlay dialog-overlay--compact" onMouseDown={onCancel}>
      <div
        className="dialog-card"
        role="dialog"
        aria-modal="true"
        aria-labelledby={labelledBy}
        onMouseDown={(e) => e.stopPropagation()}
      >
        {children}
      </div>
    </div>,
    document.body,
  );
}

// CONFIRM_DIALOGS: добавлено 2026-09-24 — третья кнопка нужна диалогам вида «Сохранить / Не
// сохранять / Отмена» (переключение чата с несохранёнными файлами), а `busy` блокирует кнопки,
// пока подтверждённое действие ещё выполняется, чтобы двойной клик не запускал его дважды.
export interface ConfirmDialogExtraAction {
  label: string;
  onClick: () => void;
  danger?: boolean;
}

export interface ConfirmDialogProps {
  title: string;
  message?: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  danger?: boolean;
  /** Optional third button rendered between Cancel and Confirm. */
  extraAction?: ConfirmDialogExtraAction;
  /** Disables every button while the confirmed action is still running. */
  busy?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

export function ConfirmDialog({
  title,
  message,
  confirmLabel,
  cancelLabel,
  danger,
  extraAction,
  busy,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  const { t } = useTranslation();
  const titleId = useId();
  const confirmText = confirmLabel ?? t('common.confirm');
  const cancelText = cancelLabel ?? t('common.cancel');
  const cancelRef = useRef<HTMLButtonElement>(null);
  const confirmRef = useRef<HTMLButtonElement>(null);

  // A destructive dialog focuses Cancel, so a reflexive Enter never deletes anything.
  useEffect(() => {
    (danger ? cancelRef : confirmRef).current?.focus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <DialogShell onCancel={busy ? () => {} : onCancel} labelledBy={titleId}>
      <div className="dialog-title" id={titleId}>
        {title}
      </div>
      {message && <div className="dialog-message">{message}</div>}
      <div className="dialog-actions">
        <button ref={cancelRef} className="dialog-btn" onClick={onCancel} type="button" disabled={busy}>
          {cancelText}
        </button>
        {extraAction && (
          <button
            className={`dialog-btn ${extraAction.danger ? 'dialog-btn--danger-outline' : ''}`}
            onClick={extraAction.onClick}
            type="button"
            disabled={busy}
          >
            {extraAction.label}
          </button>
        )}
        <button
          ref={confirmRef}
          className={`dialog-btn dialog-btn--primary ${danger ? 'dialog-btn--danger' : ''}`}
          onClick={onConfirm}
          type="button"
          disabled={busy}
        >
          {confirmText}
        </button>
      </div>
    </DialogShell>
  );
}

export interface PromptDialogProps {
  title: string;
  message?: string;
  placeholder?: string;
  initialValue?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  onSubmit: (value: string) => void;
  onCancel: () => void;
}

export function PromptDialog({
  title,
  message,
  placeholder,
  initialValue = '',
  confirmLabel,
  cancelLabel,
  onSubmit,
  onCancel,
}: PromptDialogProps) {
  const { t } = useTranslation();
  const titleId = useId();
  const confirmText = confirmLabel ?? t('common.ok');
  const cancelText = cancelLabel ?? t('common.cancel');
  const [value, setValue] = useState(initialValue);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    inputRef.current?.focus();
  }, []);

  function submit() {
    const v = value.trim();
    if (!v) return;
    onSubmit(v);
  }

  return (
    <DialogShell onCancel={onCancel} labelledBy={titleId}>
      <div className="dialog-title" id={titleId}>
        {title}
      </div>
      {message && <div className="dialog-message">{message}</div>}
      <input
        ref={inputRef}
        className="dialog-input"
        value={value}
        placeholder={placeholder}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') submit();
        }}
      />
      <div className="dialog-actions">
        <button className="dialog-btn" onClick={onCancel} type="button">
          {cancelText}
        </button>
        <button className="dialog-btn dialog-btn--primary" onClick={submit} disabled={!value.trim()} type="button">
          {confirmText}
        </button>
      </div>
    </DialogShell>
  );
}
