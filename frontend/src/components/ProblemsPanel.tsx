import { useCallback, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { analyzeProblems } from '../api/conexyApi';
import type { ProblemItem } from '../types/api';

// PROBLEMS_PANEL: добавлено 2026-10-04 — единая панель ошибок и предупреждений всего проекта
// (компилятор/линтер/типы), собранных из вывода проверки в песочнице.
interface ProblemsPanelProps {
  sessionId?: string;
  /** True while the Problems tab is selected — analysis starts on first open. */
  active: boolean;
  onOpenProblem?: (path: string, line: number) => void;
}

export function ProblemsPanel({ sessionId, active, onOpenProblem }: ProblemsPanelProps) {
  const { t } = useTranslation();
  const [problems, setProblems] = useState<ProblemItem[]>([]);
  const [errors, setErrors] = useState(0);
  const [warnings, setWarnings] = useState(0);
  const [tool, setTool] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [analyzed, setAnalyzed] = useState(false);

  const analyze = useCallback(async () => {
    if (!sessionId) return;
    setLoading(true);
    setError(null);
    try {
      const res = await analyzeProblems(sessionId);
      setProblems(res.problems ?? []);
      setErrors(res.errors ?? 0);
      setWarnings(res.warnings ?? 0);
      setTool(res.tool ?? null);
      setAnalyzed(true);
      if (!res.success) setError(res.error ?? t('workspace.problemsFailed'));
    } catch {
      setError(t('workspace.problemsFailed'));
    } finally {
      setLoading(false);
    }
  }, [sessionId, t]);

  // Runs once when the tab is first opened (the panel is remounted per chat via `key`).
  useEffect(() => {
    if (active && sessionId && !analyzed) void analyze();
  }, [active, sessionId, analyzed, analyze]);

  const grouped = groupByFile(problems);

  return (
    <div className="problems">
      <div className="problems__toolbar">
        <button
          className="dialog-btn problems__analyze"
          onClick={() => void analyze()}
          disabled={loading || !sessionId}
          type="button"
        >
          {loading ? t('workspace.problemsAnalyzing') : t('workspace.problemsAnalyze')}
        </button>
        <span className="problems__counts">
          <span className="problems__count problems__count--error" title={t('workspace.problemsErrors')}>{errors}</span>
          <span className="problems__count problems__count--warning" title={t('workspace.problemsWarnings')}>{warnings}</span>
          {tool && <span className="problems__tool">{tool}</span>}
        </span>
      </div>

      {error && <div className="workspace__hint workspace__hint--error">{error}</div>}
      {!sessionId && <div className="workspace__hint">{t('workspace.runTaskHint')}</div>}
      {sessionId && !loading && !error && analyzed && problems.length === 0 && (
        <div className="workspace__hint">{t('workspace.noProblems')}</div>
      )}

      <div className="problems__list">
        {grouped.map(([file, items]) => (
          <div key={file} className="problems__file">
            <div className="problems__file-name" title={file}>{file}</div>
            {items.map((p, i) => (
              <button
                key={`${file}:${p.line}:${p.column}:${i}`}
                className={`problems__row problems__row--${p.severity}`}
                onClick={() => onOpenProblem?.(p.file, p.line)}
                title={`${p.file}:${p.line}:${p.column}`}
                type="button"
              >
                <span className={`problems__sev problems__sev--${p.severity}`}>
                  {p.severity === 'error' ? '✕' : p.severity === 'warning' ? '!' : 'i'}
                </span>
                <span className="problems__msg">{p.message}</span>
                {p.code && <span className="problems__code">{p.code}</span>}
                <span className="problems__loc">
                  {p.line}:{p.column}
                </span>
              </button>
            ))}
          </div>
        ))}
      </div>
    </div>
  );
}

function groupByFile(problems: ProblemItem[]): Array<[string, ProblemItem[]]> {
  const map = new Map<string, ProblemItem[]>();
  for (const problem of problems) {
    const key = problem.file || '—';
    const list = map.get(key);
    if (list) list.push(problem);
    else map.set(key, [problem]);
  }
  return Array.from(map.entries());
}
