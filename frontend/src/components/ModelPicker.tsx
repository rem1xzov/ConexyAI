import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { ConexyModel, ReasoningEffort } from '../types/api';
import { CheckIcon, ChevronDownIcon } from './Icons';

interface ModelOption {
  value: ConexyModel;
  label: string;
  shortLabel: string;
  descKey: string;
}

const FLASH_MODEL: ModelOption = {
  value: 'ConexyV1-flash',
  label: 'ConexyV1-flash',
  shortLabel: 'Flash',
  descKey: 'model.flashDesc',
};

const PRO_MODEL: ModelOption = {
  value: 'ConexyV1-pro',
  label: 'ConexyV1-pro',
  shortLabel: 'Pro',
  descKey: 'model.proDesc',
};

const CODER_MODEL: ModelOption = {
  value: 'conexy-coder',
  label: 'conexy-coder',
  shortLabel: 'Coder',
  descKey: 'model.coderDesc',
};

const ALL_MODELS: ModelOption[] = [FLASH_MODEL, PRO_MODEL, CODER_MODEL];
const REASONING_LEVELS: ReasoningEffort[] = ['low', 'high', 'max'];

interface ModelPickerProps {
  model: ConexyModel;
  onModelChange: (model: ConexyModel) => void;
  mode: 'chat' | 'code';
  thinking: boolean;
  onThinkingChange: (value: boolean) => void;
  reasoningEffort: ReasoningEffort;
  onReasoningEffortChange: (value: ReasoningEffort) => void;
  smartSearch: boolean;
  onSmartSearchChange: (value: boolean) => void;
  locked?: boolean;
}

export function ModelPicker({
  model,
  onModelChange,
  mode,
  thinking,
  onThinkingChange,
  reasoningEffort,
  onReasoningEffortChange,
  smartSearch,
  onSmartSearchChange,
  locked,
}: ModelPickerProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const { t } = useTranslation();

  useEffect(() => {
    function onClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    document.addEventListener('mousedown', onClick);
    return () => document.removeEventListener('mousedown', onClick);
  }, []);

  const options = mode === 'code' ? [CODER_MODEL] : [FLASH_MODEL, PRO_MODEL];
  const current = ALL_MODELS.find((o) => o.value === model) ?? options[0];
  const isAgent = mode === 'code';
  const showThinking = !isAgent && model === 'ConexyV1-pro';
  const showSmartSearch = !isAgent;

  function select(m: ModelOption) {
    onModelChange(m.value);
    setOpen(false);
  }

  return (
    <div className="modelpicker" ref={ref}>
      <button
        className="modelpicker__trigger"
        onClick={() => !locked && setOpen((o) => !o)}
        disabled={locked}
        type="button"
      >
        <span className="modelpicker__label-full">{current.label}</span>
        <span className="modelpicker__label-short">{current.shortLabel}</span>
        <span className="chevron">
          <ChevronDownIcon size={14} />
        </span>
      </button>

      {open && (
        <div className="modelpicker__menu">
          {options.map((m) => (
            <button key={m.value} className="modelpicker__item" onClick={() => select(m)} type="button">
              <span className="modelpicker__name">
                {m.label}
                {m.value === model && (
                  <span className="modelpicker__check">
                    <CheckIcon size={14} />
                  </span>
                )}
              </span>
              <span className="modelpicker__desc">{t(m.descKey)}</span>
            </button>
          ))}

          {showThinking && (
            <>
              <div className="modelpicker__divider" />
              <button
                className="modelpicker__thinking"
                onClick={() => onThinkingChange(!thinking)}
                type="button"
              >
                <span className={`modelpicker__checkbox ${thinking ? 'modelpicker__checkbox--on' : ''}`}>
                  {thinking ? <CheckIcon size={12} /> : ''}
                </span>
                <span className="modelpicker__thinking-label">
                  <span className="modelpicker__thinking-title">{t('model.thinking')}</span>
                  <span className="modelpicker__thinking-desc">{t('model.thinkingDesc')}</span>
                </span>
              </button>
            </>
          )}

          {showSmartSearch && (
            <>
              <div className="modelpicker__divider" />
              <button
                className="modelpicker__thinking"
                onClick={() => onSmartSearchChange(!smartSearch)}
                type="button"
              >
                <span className={`modelpicker__checkbox ${smartSearch ? 'modelpicker__checkbox--on' : ''}`}>
                  {smartSearch ? <CheckIcon size={12} /> : ''}
                </span>
                <span className="modelpicker__thinking-label">
                  <span className="modelpicker__thinking-title">{t('model.smartSearch')}</span>
                  <span className="modelpicker__thinking-desc">{t('model.smartSearchDesc')}</span>
                </span>
              </button>
            </>
          )}

          {isAgent && (
            <>
              <div className="modelpicker__divider" />
              <div className="modelpicker__reasoning">
                <span className="modelpicker__thinking-title">{t('model.reasoningDepth')}</span>
                <div className="modelpicker__reasoning-options">
                  {REASONING_LEVELS.map((level) => (
                    <button
                      key={level}
                      className={`modelpicker__reasoning-option ${reasoningEffort === level ? 'modelpicker__reasoning-option--on' : ''}`}
                      onClick={() => onReasoningEffortChange(level)}
                      type="button"
                    >
                      {level}
                    </button>
                  ))}
                </div>
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}
