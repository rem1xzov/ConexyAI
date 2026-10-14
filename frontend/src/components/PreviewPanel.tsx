import { useCallback, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { getPreview, getPreviewLogs, stopPreview, type PreviewState } from '../api/conexyApi';

// PREVIEW: вкладка «Превью» — поддомен, токен и логи сервиса, который агент запустил через start_service.
interface PreviewPanelProps {
  sessionId?: string;
  /** True while the Preview tab is selected — state is polled only then. */
  active: boolean;
}

export function PreviewPanel({ sessionId, active }: PreviewPanelProps) {
  const { t } = useTranslation();
  const [state, setState] = useState<PreviewState | null>(null);
  const [logs, setLogs] = useState('');
  const [showToken, setShowToken] = useState(false);

  const refresh = useCallback(async () => {
    if (!sessionId) return;
    try {
      const next = await getPreview(sessionId);
      setState(next);
      setLogs(next.running ? await getPreviewLogs(sessionId) : '');
    } catch {
      setState(null);
    }
  }, [sessionId]);

  useEffect(() => {
    if (!active || !sessionId) return;
    void refresh();
    const timer = window.setInterval(() => void refresh(), 5000);
    return () => window.clearInterval(timer);
  }, [active, sessionId, refresh]);

  async function stop() {
    if (!sessionId) return;
    await stopPreview(sessionId);
    await refresh();
  }

  if (!sessionId || !state?.running) {
    return <div className="workspace__hint">{t('workspace.previewEmpty')}</div>;
  }

  return (
    <div className="problems">
      <div className="problems__toolbar">
        <a className="dialog-btn" href={state.openUrl ?? '#'} target="_blank" rel="noopener noreferrer">
          {t('workspace.previewOpen')}
        </a>
        <button className="dialog-btn" type="button" onClick={() => void navigator.clipboard.writeText(state.openUrl ?? '')}>
          {t('workspace.previewCopy')}
        </button>
        <button className="dialog-btn" type="button" onClick={() => void refresh()}>
          {t('workspace.previewRefresh')}
        </button>
        <button className="dialog-btn" type="button" onClick={() => void stop()}>
          {t('workspace.previewStop')}
        </button>
      </div>
      <div className="workspace__hint">
        <div>
          {t('workspace.previewUrl')}: <code>{state.url}</code> (:{state.port})
        </div>
        <div>
          {t('workspace.previewToken')}:{' '}
          <code>{showToken ? state.token : '••••••••••••••••'}</code>{' '}
          <button className="dialog-btn" type="button" onClick={() => setShowToken((v) => !v)}>
            {showToken ? '🙈' : '👁'}
          </button>
        </div>
        {state.command && <div><code>{state.command}</code></div>}
      </div>
      <pre className="workspace__hint" style={{ whiteSpace: 'pre-wrap', maxHeight: 240, overflow: 'auto' }}>
        {logs || t('workspace.previewLogs')}
      </pre>
    </div>
  );
}
