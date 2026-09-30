import { Component, useEffect, useRef, useState, type ErrorInfo, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import {
  artifactFileName,
  artifactMime,
  hasPreview,
  svgPreviewDocument,
  type ArtifactKind,
} from '../../utils/artifacts';
import { triggerDownload } from '../../utils/download';
import { ChevronDownIcon, CloseIcon, DownloadIcon } from '../Icons';
import { Markdown } from '../Markdown';
import { CopyButton } from '../markdown/CodeBlock';
import { HighlightedCode } from '../markdown/HighlightedCode';
import { MermaidDiagram } from '../markdown/MermaidDiagram';
import { ArtifactIcon } from './ArtifactIcon';
import { artifactTypeLabel } from './ArtifactCard';
import {
  closeArtifact,
  getArtifactState,
  resolveOpenArtifact,
  selectArtifactVersion,
  setArtifactTab,
  useArtifactState,
  type ArtifactTab,
} from './store';

/**
 * ARTIFACTS: добавлено 2026-09-24 — правая выдвижная панель артефакта (контракт C-9).
 *
 * Вкладки «Превью» / «Код», копирование, скачивание, переключатель версий, закрытие (кнопка или
 * Esc). На десктопе — панель ~46vw поверх правой части экрана, на телефоне — во весь экран.
 *
 * Превью HTML и SVG живёт в <iframe sandbox="allow-scripts" srcdoc>: без allow-same-origin у
 * документа уникальный «opaque» origin, поэтому скрипты артефакта работают (калькулятор
 * кликабелен), но не видят ни токен, ни localStorage, ни DOM приложения и не могут
 * навигировать вкладку.
 */

const CODE_LANGUAGE: Record<ArtifactKind, string | undefined> = {
  'text/html': 'html',
  'image/svg+xml': 'svg',
  'text/mermaid': 'mermaid',
  'text/markdown': 'markdown',
  'application/code': undefined,
};

function useIsMobile(): boolean {
  const query = '(max-width: 768px)';
  const [mobile, setMobile] = useState(() => typeof window.matchMedia === 'function' && window.matchMedia(query).matches);
  useEffect(() => {
    if (typeof window.matchMedia !== 'function') return;
    const mql = window.matchMedia(query);
    const onChange = () => setMobile(mql.matches);
    mql.addEventListener?.('change', onChange);
    return () => mql.removeEventListener?.('change', onChange);
  }, []);
  return mobile;
}

function PreviewFrame({ type, content, title }: { type: ArtifactKind; content: string; title: string }) {
  const srcDoc = type === 'image/svg+xml' ? svgPreviewDocument(content) : content;
  return (
    <iframe
      className="artifact-panel__frame"
      title={title}
      sandbox="allow-scripts"
      srcDoc={srcDoc}
      referrerPolicy="no-referrer"
    />
  );
}

function ArtifactPanel() {
  const { t } = useTranslation();
  const state = useArtifactState();
  const resolved = resolveOpenArtifact(state);
  const mobile = useIsMobile();
  const panelRef = useRef<HTMLElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const returnFocusRef = useRef<HTMLElement | null>(null);
  const lastFocusNonce = useRef(state.focusNonce);

  const isOpen = resolved !== null;

  // Focus: an explicit open moves focus into the panel (and remembers where it came from); an
  // automatic open during streaming never steals focus from the composer.
  useEffect(() => {
    if (!isOpen) return;
    if (state.focusNonce === lastFocusNonce.current) return;
    lastFocusNonce.current = state.focusNonce;
    const active = document.activeElement;
    if (active instanceof HTMLElement && !panelRef.current?.contains(active)) returnFocusRef.current = active;
    closeRef.current?.focus({ preventScroll: true });
  }, [isOpen, state.focusNonce]);

  // Give focus back when the panel closes.
  useEffect(() => {
    if (isOpen) return;
    const target = returnFocusRef.current;
    returnFocusRef.current = null;
    if (target && document.contains(target)) target.focus({ preventScroll: true });
  }, [isOpen]);

  // Esc closes — unless another modal (lightbox, dialog) is on top and owns the key.
  useEffect(() => {
    if (!isOpen) return;
    function onKeyDown(e: KeyboardEvent) {
      if (e.key !== 'Escape' || e.defaultPrevented) return;
      const modal = document.querySelector('[aria-modal="true"]');
      if (modal && !panelRef.current?.contains(modal)) return;
      e.preventDefault();
      closeArtifact();
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [isOpen]);

  // The artifact disappeared (its chat was closed): close the panel instead of showing nothing.
  useEffect(() => {
    if (!state.open || resolved) return;
    const timer = window.setTimeout(() => {
      const latest = getArtifactState();
      if (latest.open && !resolveOpenArtifact(latest)) closeArtifact();
    }, 0);
    return () => window.clearTimeout(timer);
  }, [state.open, resolved]);

  // Freeze the page behind the full-screen mobile panel.
  useEffect(() => {
    if (!isOpen || !mobile) return;
    const previous = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = previous;
    };
  }, [isOpen, mobile]);

  if (!resolved) return null;

  const { artifact, versions, current } = resolved;
  const previewable = hasPreview(artifact.type);
  const tab: ArtifactTab = previewable ? state.tab : 'code';
  const title = artifact.title || t('render.artifactUntitled');
  const versionIndex = current ? versions.findIndex((v) => v.key === current.key) : -1;
  const codeLanguage = CODE_LANGUAGE[artifact.type] ?? artifact.language;

  function download() {
    const name = artifactFileName({
      title: artifact.title,
      identifier: artifact.identifier ?? 'artifact',
      type: artifact.type,
      language: artifact.language,
    });
    triggerDownload(new Blob([artifact.content], { type: artifactMime(artifact.type) }), name);
  }

  let preview;
  if (!artifact.complete) {
    preview = <div className="artifact-panel__placeholder">{t('render.previewPending')}</div>;
  } else if (artifact.type === 'text/html' || artifact.type === 'image/svg+xml') {
    preview = <PreviewFrame type={artifact.type} content={artifact.content} title={t('render.previewFrameTitle', { title })} />;
  } else if (artifact.type === 'text/mermaid') {
    preview = <MermaidDiagram code={artifact.content} closed variant="panel" />;
  } else if (artifact.type === 'text/markdown') {
    preview = (
      <div className="artifact-panel__doc chat-text">
        <Markdown text={artifact.content} />
      </div>
    );
  }

  const panel = (
    <aside
      ref={panelRef}
      className={`artifact-panel ${mobile ? 'artifact-panel--mobile' : ''}`}
      role="dialog"
      aria-modal={mobile ? 'true' : 'false'}
      aria-labelledby="artifact-panel-title"
    >
      <header className="artifact-panel__header">
        <div className="artifact-panel__heading">
          <span className="artifact-panel__icon" aria-hidden="true">
            <ArtifactIcon type={artifact.type} size={18} />
          </span>
          <div className="artifact-panel__titles">
            <h2 id="artifact-panel-title" className="artifact-panel__title" title={title}>
              {title}
            </h2>
            <span className="artifact-panel__subtitle">
              {artifact.complete ? artifactTypeLabel(t, artifact.type, artifact.language) : t('render.artifactCreating')}
            </span>
          </div>
          <button
            ref={closeRef}
            type="button"
            className="artifact-panel__iconbtn"
            onClick={closeArtifact}
            aria-label={t('render.close')}
            title={t('render.close')}
          >
            <CloseIcon size={18} />
          </button>
        </div>

        <div className="artifact-panel__toolbar">
          {previewable ? (
            <div className="artifact-panel__tabs" role="tablist" aria-label={t('render.viewMode')}>
              {(['preview', 'code'] as const).map((id) => (
                <button
                  key={id}
                  type="button"
                  role="tab"
                  id={`artifact-tab-${id}`}
                  aria-selected={tab === id}
                  aria-controls="artifact-panel-body"
                  className={`artifact-panel__tab ${tab === id ? 'artifact-panel__tab--active' : ''}`}
                  onClick={() => setArtifactTab(id)}
                >
                  {id === 'preview' ? t('render.preview') : t('render.codeTab')}
                </button>
              ))}
            </div>
          ) : (
            <span />
          )}

          <div className="artifact-panel__actions">
            {versions.length > 1 && versionIndex >= 0 && (
              <div className="artifact-panel__versions" aria-label={t('render.versions')}>
                <button
                  type="button"
                  className="artifact-panel__iconbtn"
                  onClick={() => selectArtifactVersion(versions[versionIndex - 1].key)}
                  disabled={versionIndex === 0}
                  aria-label={t('render.prevVersion')}
                  title={t('render.prevVersion')}
                >
                  <ChevronDownIcon size={14} className="rotate-90" />
                </button>
                <span className="artifact-panel__version">
                  {t('render.versionOf', { n: versionIndex + 1, total: versions.length })}
                </span>
                <button
                  type="button"
                  className="artifact-panel__iconbtn"
                  onClick={() => selectArtifactVersion(versions[versionIndex + 1].key)}
                  disabled={versionIndex === versions.length - 1}
                  aria-label={t('render.nextVersion')}
                  title={t('render.nextVersion')}
                >
                  <ChevronDownIcon size={14} className="-rotate-90" />
                </button>
              </div>
            )}
            <CopyButton text={artifact.content} className="artifact-panel__btn" />
            <button
              type="button"
              className="artifact-panel__btn"
              onClick={download}
              disabled={!artifact.complete}
              title={t('render.download')}
            >
              <DownloadIcon size={14} />
              <span>{t('render.download')}</span>
            </button>
          </div>
        </div>
      </header>

      <div
        id="artifact-panel-body"
        className={`artifact-panel__body artifact-panel__body--${tab}`}
        role={previewable ? 'tabpanel' : undefined}
        aria-labelledby={previewable ? `artifact-tab-${tab}` : undefined}
      >
        {tab === 'preview' && previewable ? (
          preview
        ) : (
          <pre className="artifact-panel__code">
            <HighlightedCode code={artifact.content} language={codeLanguage} />
          </pre>
        )}
      </div>
    </aside>
  );

  return createPortal(panel, document.body);
}

interface PanelBoundaryState {
  error: Error | null;
}

/** Keeps a broken artifact from taking the whole app down: the panel shows the error instead. */
class PanelBoundary extends Component<
  { children: ReactNode; closeLabel: string; title: string; resetKey: string },
  PanelBoundaryState
> {
  state: PanelBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): PanelBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error('[ArtifactPanel] render failed:', error, info.componentStack);
  }

  componentDidUpdate(prev: { resetKey: string }): void {
    // Opening something else (or closing) starts from a clean slate after an error.
    if (this.state.error && prev.resetKey !== this.props.resetKey) this.setState({ error: null });
  }

  render() {
    if (!this.state.error) return this.props.children;
    return createPortal(
      <aside className="artifact-panel" role="dialog" aria-modal="false" aria-label={this.props.title}>
        <div className="artifact-panel__placeholder">
          <p>{this.state.error.message}</p>
          <button type="button" className="artifact-panel__btn" onClick={closeArtifact}>
            {this.props.closeLabel}
          </button>
        </div>
      </aside>,
      document.body,
    );
  }
}

/** Mounted once from main.tsx next to <App/>, so App.tsx needs no changes. */
export function ArtifactPanelHost() {
  const { t } = useTranslation();
  const state = useArtifactState();
  const resetKey = state.open ? `${state.open.identifier ?? 'adhoc'}|${state.focusNonce}` : 'closed';
  return (
    <PanelBoundary resetKey={resetKey} closeLabel={t('render.close')} title={t('render.panelLabel')}>
      <ArtifactPanel />
    </PanelBoundary>
  );
}
