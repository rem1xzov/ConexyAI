import { useTranslation } from 'react-i18next';
import type { DeviceStatus } from '../api/deviceApi';
import type { ExecutionTarget } from '../utils/deviceTarget';

// WEB_DEVICE: добавлено 2026-10-18 — переключатель «Песочница / Мой ПК» в композере агентских
// режимов (Coder / Cowork), на ПК и на телефоне.
interface DeviceTargetBarProps {
  target: ExecutionTarget;
  onTargetChange: (target: ExecutionTarget) => void;
  status: DeviceStatus | null;
  /** False — the user never issued a desktop key, so there is no PC to run on. */
  hasKey: boolean | null;
  projectId: string | null;
  onProjectChange: (projectId: string) => void;
  onOpenSetup: () => void;
  disabled?: boolean;
}

export function DeviceTargetBar({
  target,
  onTargetChange,
  status,
  hasKey,
  projectId,
  onProjectChange,
  onOpenSetup,
  disabled,
}: DeviceTargetBarProps) {
  const { t } = useTranslation();
  const neverSetUp = hasKey === false && !status?.name;
  const projects = status?.projects ?? [];

  return (
    <div className="device-target">
      <div className="device-target__switch" role="radiogroup" aria-label={t('device.target')}>
        {(['sandbox', 'device'] as const).map((value) => (
          <button
            key={value}
            type="button"
            role="radio"
            aria-checked={target === value}
            className={`device-target__opt ${target === value ? 'device-target__opt--active' : ''}`}
            onClick={() => onTargetChange(value)}
            disabled={disabled}
          >
            {value === 'sandbox' ? t('device.sandbox') : t('device.myPc')}
          </button>
        ))}
      </div>

      {target === 'device' &&
        (neverSetUp ? (
          <button type="button" className="device-target__setup" onClick={onOpenSetup}>
            {t('device.setUp')}
          </button>
        ) : (
          <>
            <span
              className="device-target__status"
              title={status?.online ? t('device.online') : t('device.offline')}
            >
              <span
                className={`device-target__dot ${status?.online ? 'device-target__dot--online' : ''}`}
                aria-hidden="true"
              />
              <span className="device-target__name">{status?.name || t('device.unknownPc')}</span>
              {!status?.online && <span className="device-target__offline">· {t('device.offline')}</span>}
            </span>
            {projects.length > 0 && (
              <select
                className="device-target__project"
                value={projectId ?? projects[0].id}
                onChange={(e) => onProjectChange(e.target.value)}
                aria-label={t('device.project')}
                disabled={disabled}
              >
                {projects.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </select>
            )}
          </>
        ))}
    </div>
  );
}
