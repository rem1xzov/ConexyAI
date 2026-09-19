import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

function DialogShell({ onCancel, children }: { onCancel: () => void; children: ReactNode }) {
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onCancel();
    }
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onCancel]);

  return (
    <div className="dialog-overlay" onMouseDown={onCancel}>
      <div className="dialog-card" role="dialog" aria-modal="true" onMouseDown={(e) => e.stopPropagation()}>
        {children}
      </div>
    </div>
  );
}

export interface ConfirmDialogProps {
  title: string;
  message?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  danger?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

export function ConfirmDialog({
  title,
  message,
  confirmLabel,
  cancelLabel,
  danger,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  const { t } = useTranslation();
  const confirmText = confirmLabel ?? t('common.confirm');
  const cancelText = cancelLabel ?? t('common.cancel');
  return (
    <DialogShell onCancel={onCancel}>
      <div className="dialog-title">{title}</div>
      {message && <div className="dialog-message">{message}</div>}
      <div className="dialog-actions">
        <button className="dialog-btn" onClick={onCancel} type="button">
          {cancelText}
        </button>
        <button
          className={`dialog-btn dialog-btn--primary ${danger ? 'dialog-btn--danger' : ''}`}
          onClick={onConfirm}
          type="button"
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
    <DialogShell onCancel={onCancel}>
      <div className="dialog-title">{title}</div>
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
