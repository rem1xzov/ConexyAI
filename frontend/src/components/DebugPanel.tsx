import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { runDebug } from '../api/conexyApi';
import type { DebugResult, DebugStep } from '../types/api';

// DEBUG_TRACE: добавлено 2026-10-05 — панель отладки «уровень A»: точки останова (кликом в гаттере),
// запуск скрипта под trace-harness в песочнице и просмотр шагов выполнения с переменными и стеком.
interface DebugPanelProps {
  sessionId?: string;
  active: boolean;
  /** Workspace-relative path used as the entry script (usually the active file). */
  entryPath: string | null;
  /** path → breakpoint lines. */
  breakpoints: Record<string, number[]>;
  onRemoveBreakpoint: (path: string, line: number) => void;
  onOpenLocation: (path: string, line: number, column: number) => void;
}

export function DebugPanel({ sessionId, entryPath, breakpoints, onRemoveBreakpoint, onOpenLocation }: DebugPanelProps) {
  const { t } = useTranslation();
  const [recordAll, setRecordAll] = useState(false);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<DebugResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState(0);

  const flat = useMemo(
    () => Object.entries(breakpoints).flatMap(([path, lines]) => lines.map((line) => ({ path, line }))),
    [breakpoints],
  );

  const isPython = !!entryPath && entryPath.toLowerCase().endsWith('.py');

  async function run() {
    if (!sessionId || !entryPath || !isPython) return;
    setLoading(true);
    setError(null);
    try {
      const res = await runDebug(sessionId, entryPath, flat, recordAll);
      setResult(res);
      setSelected(0);
      if (!res.success) setError(errorText(res.error, t));
    } catch {
      setError(t('debug.errFailed'));
    } finally {
      setLoading(false);
    }
  }

  const steps = result?.steps ?? [];
  const current: DebugStep | undefined = steps[selected];

  return (
    <div className="debug">
      <div className="debug__toolbar">
        <span className="debug__entry" title={entryPath ?? ''}>
          {entryPath ? entryPath.split('/').pop() : t('debug.noEntryShort')}
        </span>
        <label className="debug__toggle" title={t('debug.recordAllHint')}>
          <input type="checkbox" checked={recordAll} onChange={(e) => setRecordAll(e.target.checked)} />
          {t('debug.recordAll')}
        </label>
        <button
          className="dialog-btn dialog-btn--primary debug__run"
          onClick={() => void run()}
          disabled={loading || !sessionId || !isPython}
          type="button"
        >
          {loading ? t('debug.running') : t('debug.run')}
        </button>
      </div>

      {!isPython && <div className="workspace__hint">{t('debug.pyOnly')}</div>}
      {isPython && flat.length === 0 && <div className="workspace__hint">{t('debug.noBreakpoints')}</div>}
      {error && <div className="workspace__hint workspace__hint--error">{error}</div>}

      {flat.length > 0 && (
        <div className="debug__breakpoints">
          <span className="debug__breakpoints-title">{t('debug.breakpoints')}</span>
          {flat.map((bp) => (
            <span className="debug__bp" key={`${bp.path}:${bp.line}`}>
              <button
                className="debug__bp-path"
                onClick={() => onOpenLocation(bp.path, bp.line, 1)}
                title={bp.path}
                type="button"
              >
                {bp.path.split('/').pop()}:{bp.line}
              </button>
              <button
                className="debug__bp-remove"
                onClick={() => onRemoveBreakpoint(bp.path, bp.line)}
                title={t('debug.remove')}
                aria-label={t('debug.remove')}
                type="button"
              >
                ×
              </button>
            </span>
          ))}
        </div>
      )}

      {result && (
        <div className="debug__result">
          <div className="debug__summary">
            <span>{t('debug.stepCount', { count: steps.length })}</span>
            {result.truncated && <span className="debug__truncated">{t('debug.truncated')}</span>}
          </div>

          {steps.length === 0 && !result.programError && <div className="workspace__hint">{t('debug.noSteps')}</div>}

          {steps.length > 0 && (
            <div className="debug__split">
              <div className="debug__list">
                {steps.map((step, index) => (
                  <button
                    key={`${index}:${step.file}:${step.line}`}
                    className={`debug__step ${index === selected ? 'debug__step--active' : ''}`}
                    onClick={() => {
                      setSelected(index);
                      onOpenLocation(step.file, step.line, 1);
                    }}
                    type="button"
                  >
                    {step.breakpoint && <span className="debug__step-bp" aria-hidden />}
                    <span className="debug__step-fn">{step.function}</span>
                    <span className="debug__step-loc">
                      {step.file.split('/').pop()}:{step.line}
                    </span>
                  </button>
                ))}
              </div>

              {current && (
                <div className="debug__details">
                  <div className="debug__details-section">
                    <div className="debug__details-title">{t('debug.variables')}</div>
                    {Object.keys(current.locals).length === 0 && <div className="debug__empty">—</div>}
                    {Object.entries(current.locals).map(([name, value]) => (
                      <div className="debug__var" key={name}>
                        <span className="debug__var-name">{name}</span>
                        <span className="debug__var-value" title={value}>
                          {value}
                        </span>
                      </div>
                    ))}
                  </div>
                  <div className="debug__details-section">
                    <div className="debug__details-title">{t('debug.callStack')}</div>
                    {current.stack.map((frame, index) => (
                      <div className="debug__frame" key={`${index}:${frame}`}>
                        {frame}
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}

          {result.programError && (
            <div className="debug__details-section">
              <div className="debug__details-title">{t('debug.programError')}</div>
              <pre className="debug__pre debug__pre--error">{result.programError}</pre>
            </div>
          )}

          {result.output.trim().length > 0 && (
            <div className="debug__details-section">
              <div className="debug__details-title">{t('debug.output')}</div>
              <pre className="debug__pre">{result.output}</pre>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function errorText(code: string | null | undefined, t: (key: string) => string): string {
  switch (code) {
    case 'NO_WORKSPACE':
      return t('debug.errNoWorkspace');
    case 'NO_ENTRY':
      return t('debug.errNoEntry');
    case 'UNSUPPORTED_LANGUAGE':
      return t('debug.errUnsupported');
    case 'ENTRY_NOT_FOUND':
      return t('debug.errNotFound');
    case 'TIMEOUT':
      return t('debug.errTimeout');
    case 'SANDBOX_UNAVAILABLE':
      return t('debug.errSandbox');
    default:
      return t('debug.errFailed');
  }
}
