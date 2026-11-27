import type { CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import type { DeviceStatus } from '../api/deviceApi';
import { ChevronDownIcon, FolderIcon } from './Icons';

// VISUAL_DESKTOP: визуальный десктоп (`/desktop`) — это тот же веб-клиент, но агент в нём всегда
// работает на ПК пользователя. Поэтому выбора «Песочница / Мой ПК» там нет: вместо него в меню
// моделей показывается рабочая область — папка на ПК, в которой агент выполняет команды и правит
// файлы. Разметка переиспользует классы `.device-target*` (стили живут в styles/index.css и общие
// для обоих вариантов); новые строки и отступы заданы точечно инлайном.
interface WorkspacePickerProps {
  status: DeviceStatus | null;
  /** Persisted selection; when it is missing or stale the first project is shown. */
  projectId: string | null;
  onProjectChange: (projectId: string) => void;
  disabled?: boolean;
}

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

const selectStyle: CSSProperties = {
  margin: '2px 10px 6px',
  maxWidth: 'calc(100% - 20px)',
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

/** Current workspace: `<select>` of the connected PC's project folders plus a short hint. */
export function WorkspaceSection({ status, projectId, onProjectChange, disabled }: WorkspacePickerProps) {
  const { t } = useTranslation();
  const projects = status?.projects ?? [];
  const current = projects.some((p) => p.id === projectId) ? projectId : (projects[0]?.id ?? null);

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

      {projects.length > 0 ? (
        <>
          <select
            className="device-target__project"
            style={selectStyle}
            value={current ?? ''}
            onChange={(e) => onProjectChange(e.target.value)}
            aria-label={t('device.workspaceSelect')}
            title={t('device.workspaceHint')}
            disabled={disabled}
          >
            {projects.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
          <span style={hintStyle}>{t('device.workspaceHint')}</span>
        </>
      ) : status ? (
        // Статус уже загружен и папок действительно нет — подсказываем, как добавить папку с ПК.
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
 * switchable without opening the model sheet. Rendered next to the mode chip; returns nothing while
 * the PC reports no folders — the model sheet then carries the "add a folder" hint.
 */
export function WorkspaceChip({ status, projectId, onProjectChange, disabled }: WorkspacePickerProps) {
  const { t } = useTranslation();
  const projects = status?.projects ?? [];
  if (projects.length === 0) return null;
  const current = projects.some((p) => p.id === projectId) ? projectId : projects[0].id;

  return (
    <span
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 4,
        flex: '0 0 auto',
        minWidth: 0,
        maxWidth: 170,
        padding: '6px 10px',
        border: '1px solid var(--border)',
        borderRadius: 999,
        background: 'var(--surface-2)',
        color: 'var(--text)',
        fontSize: 12,
        whiteSpace: 'nowrap',
      }}
      title={t('device.workspaceHint')}
    >
      <FolderIcon size={14} />
      <select
        value={current ?? ''}
        onChange={(e) => onProjectChange(e.target.value)}
        aria-label={t('device.workspaceSelect')}
        disabled={disabled}
        style={{
          appearance: 'none',
          minWidth: 0,
          maxWidth: 128,
          padding: 0,
          border: 'none',
          background: 'transparent',
          color: 'inherit',
          font: 'inherit',
          textOverflow: 'ellipsis',
          overflow: 'hidden',
          cursor: 'pointer',
          outline: 'none',
        }}
      >
        {projects.map((p) => (
          <option key={p.id} value={p.id}>
            {p.name}
          </option>
        ))}
      </select>
      {/* Стрелка — только подсказка: клик по ней уходит в сам `<select>` под ней. */}
      <span style={{ display: 'inline-flex', flex: 'none', color: 'var(--muted)', pointerEvents: 'none' }}>
        <ChevronDownIcon size={12} />
      </span>
    </span>
  );
}
