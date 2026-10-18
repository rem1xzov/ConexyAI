import { useTranslation } from 'react-i18next';
import type { DeviceStatus } from '../api/deviceApi';
import type { ExecutionTarget } from '../utils/deviceTarget';
import { CheckIcon } from './Icons';

// WEB_DEVICE: добавлено 2026-10-18 — «Песочница / Мой ПК» для Coder / Cowork.
// Раздел меню моделей (ModelPicker): две галочки-варианта, на ПК и на телефоне одинаково.
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

  const option = (value: ExecutionTarget, title: string, desc: React.ReactNode) => (
    <button
      type="button"
      role="radio"
      aria-checked={target === value}
      className="modelpicker__thinking device-target__opt"
      onClick={() => onTargetChange(value)}
      disabled={disabled}
    >
      <span className={`modelpicker__checkbox ${target === value ? 'modelpicker__checkbox--on' : ''}`}>
        {target === value ? <CheckIcon size={12} /> : ''}
      </span>
      <span className="modelpicker__thinking-label">
        <span className="modelpicker__thinking-title">{title}</span>
        <span className="modelpicker__thinking-desc">{desc}</span>
      </span>
    </button>
  );

  return (
    <div className="device-target" role="radiogroup" aria-label={t('device.target')}>
      <span className="device-target__heading">{t('device.target')}</span>
      {option('sandbox', t('device.sandbox'), t('device.sandboxDesc'))}
      {option(
        'device',
        t('device.myPc'),
        <span className="device-target__status">
          <span className={`device-target__dot ${status?.online ? 'device-target__dot--online' : ''}`} aria-hidden="true" />
          <span className="device-target__name">{status?.name || t('device.unknownPc')}</span>
          <span className={status?.online ? '' : 'device-target__offline'}>
            · {status?.online ? t('device.online') : t('device.offline')}
          </span>
        </span>,
      )}

      {target === 'device' &&
        (neverSetUp ? (
          <button type="button" className="device-target__setup" onClick={onOpenSetup}>
            {t('device.setUp')}
          </button>
        ) : (
          projects.length > 0 && (
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
          )
        ))}
    </div>
  );
}
