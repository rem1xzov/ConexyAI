import { memo, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { openAdhocArtifact } from '../artifacts/store';
import { CopyButton } from './CodeBlock';
import { HighlightedCode } from './HighlightedCode';

/**
 * MERMAID_DIAGRAMS: добавлено 2026-09-24 — блоки ```mermaid рисуются схемой.
 *
 * * mermaid (~мегабайты) грузится динамическим import-ом только когда на экране есть схема;
 * * securityLevel 'strict' + htmlLabels:false: подписи — обычный SVG-текст, без HTML;
 * * результат показывается как <img src="data:image/svg+xml,…">: внутри картинки браузер не
 *   исполняет скрипты и не грузит внешние ресурсы, так что даже ошибка санитайза в mermaid не
 *   даёт выполнить код в приложении;
 * * пока блок стримится (нет закрывающего ```), схема не перерисовывается на каждый токен —
 *   рендер запускается один раз, когда блок закрыт (с небольшой задержкой);
 * * тема схемы следует теме приложения (светлая / тёмная, инкогнито — тёмная).
 */

type MermaidApi = typeof import('mermaid').default;
type DiagramTheme = 'light' | 'dark';

interface RenderedDiagram {
  src: string;
  width: number;
  height: number;
}

let mermaidPromise: Promise<MermaidApi> | null = null;
function loadMermaid(): Promise<MermaidApi> {
  if (!mermaidPromise) {
    mermaidPromise = import('mermaid').then((mod) => mod.default);
    // A failed chunk load (offline, redeploy) may be retried on the next diagram.
    mermaidPromise.catch(() => {
      mermaidPromise = null;
    });
  }
  return mermaidPromise;
}

/** Thrown when the library itself could not be loaded — not cached as a diagram error. */
class LoadError extends Error {}

const results = new Map<string, RenderedDiagram | { error: string }>();
const RESULT_LIMIT = 60;
let queue: Promise<unknown> = Promise.resolve();
let counter = 0;

function cacheKey(code: string, theme: DiagramTheme): string {
  return `${theme}\u0000${code}`;
}

/** Turns mermaid's SVG string into a well-formed, explicitly sized data: URL. */
function toImage(svgMarkup: string): RenderedDiagram {
  // The HTML parser copes with the `<br>` mermaid may leave in labels; XMLSerializer then emits
  // well-formed XML, which an SVG image requires. DOMParser documents are inert (no scripts run,
  // nothing is fetched).
  const doc = new DOMParser().parseFromString(svgMarkup, 'text/html');
  const svg = doc.querySelector('svg');
  if (!svg) throw new Error('empty diagram');
  const viewBox = (svg.getAttribute('viewBox') ?? '').trim().split(/[\s,]+/).map(Number);
  let width = viewBox.length === 4 && viewBox[2] > 0 ? viewBox[2] : parseFloat(svg.getAttribute('width') ?? '') || 600;
  let height = viewBox.length === 4 && viewBox[3] > 0 ? viewBox[3] : parseFloat(svg.getAttribute('height') ?? '') || 400;
  width = Math.ceil(width);
  height = Math.ceil(height);
  svg.setAttribute('width', String(width));
  svg.setAttribute('height', String(height));
  svg.style.removeProperty('max-width');
  const xml = new XMLSerializer().serializeToString(svg);
  return { src: `data:image/svg+xml;charset=utf-8,${encodeURIComponent(xml)}`, width, height };
}

function errorText(error: unknown): string {
  const raw = error instanceof Error ? error.message : typeof error === 'string' ? error : String(error);
  return raw.length > 600 ? `${raw.slice(0, 600)}…` : raw;
}

/** Renders one diagram; calls are serialised because mermaid's config is global. */
function renderDiagram(code: string, theme: DiagramTheme): Promise<RenderedDiagram> {
  const key = cacheKey(code, theme);
  const cached = results.get(key);
  if (cached) return 'error' in cached ? Promise.reject(new Error(cached.error)) : Promise.resolve(cached);

  const run = async (): Promise<RenderedDiagram> => {
    let mermaid: MermaidApi;
    try {
      mermaid = await loadMermaid();
    } catch (error) {
      throw new LoadError(errorText(error));
    }
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      theme: theme === 'dark' ? 'dark' : 'default',
      htmlLabels: false,
      flowchart: { htmlLabels: false },
      suppressErrorRendering: true,
      logLevel: 'fatal',
      fontFamily: 'ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, Arial, sans-serif',
    });
    const id = `conexy-mermaid-${++counter}`;
    try {
      await mermaid.parse(code);
      const { svg } = await mermaid.render(id, code);
      return toImage(svg);
    } finally {
      // mermaid renders into a temporary node in <body>; make sure nothing is left behind.
      document.getElementById(id)?.remove();
      document.getElementById(`d${id}`)?.remove();
    }
  };

  const job = queue.then(run, run);
  queue = job.catch(() => undefined);
  return job.then(
    (diagram) => {
      remember(key, diagram);
      return diagram;
    },
    (error: unknown) => {
      if (!(error instanceof LoadError)) remember(key, { error: errorText(error) });
      throw error;
    },
  );
}

function remember(key: string, value: RenderedDiagram | { error: string }): void {
  if (results.size >= RESULT_LIMIT) results.delete(results.keys().next().value as string);
  results.set(key, value);
}

function currentTheme(): DiagramTheme {
  const root = document.documentElement;
  if (root.getAttribute('data-incognito') === 'true') return 'dark';
  return root.getAttribute('data-theme') === 'light' ? 'light' : 'dark';
}

/** Effective diagram theme, following both the theme switch and incognito mode. */
function useDiagramTheme(): DiagramTheme {
  const [theme, setTheme] = useState<DiagramTheme>(currentTheme);
  useEffect(() => {
    const observer = new MutationObserver(() => setTheme(currentTheme()));
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'data-incognito'] });
    return () => observer.disconnect();
  }, []);
  return theme;
}

type DiagramState =
  | { status: 'waiting' }
  | { status: 'loading' }
  | { status: 'ready'; diagram: RenderedDiagram }
  | { status: 'error'; error: string };

function initialState(code: string, theme: DiagramTheme, closed: boolean): DiagramState {
  if (!closed) return { status: 'waiting' };
  const cached = results.get(cacheKey(code, theme));
  if (!cached) return { status: 'loading' };
  return 'error' in cached ? { status: 'error', error: cached.error } : { status: 'ready', diagram: cached };
}

interface MermaidDiagramProps {
  code: string;
  /** False while the fence / artifact is still streaming: nothing is rendered until it closes. */
  closed: boolean;
  /** Render inside the artifact panel (no header, fills the width). */
  variant?: 'inline' | 'panel';
}

export const MermaidDiagram = memo(function MermaidDiagram({ code, closed, variant = 'inline' }: MermaidDiagramProps) {
  const { t } = useTranslation();
  const theme = useDiagramTheme();
  const [state, setState] = useState<DiagramState>(() => initialState(code, theme, closed));
  const [showCode, setShowCode] = useState(false);

  useEffect(() => {
    const initial = initialState(code, theme, closed);
    setState(initial);
    if (initial.status !== 'loading') return;
    let cancelled = false;
    // A short pause lets a burst of updates (e.g. the fence closing mid-stream) settle first.
    const timer = window.setTimeout(() => {
      renderDiagram(code, theme).then(
        (diagram) => !cancelled && setState({ status: 'ready', diagram }),
        (error: unknown) => !cancelled && setState({ status: 'error', error: errorText(error) }),
      );
    }, 150);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [code, theme, closed]);

  const source = (
    <pre className="code-block__pre">
      <HighlightedCode code={code} language="mermaid" />
    </pre>
  );

  let body;
  if (state.status === 'ready' && !showCode) {
    const img = (
      <img
        className="mermaid-diagram__img"
        src={state.diagram.src}
        width={state.diagram.width}
        height={state.diagram.height}
        alt={t('render.mermaidAlt')}
        draggable={false}
      />
    );
    body =
      variant === 'inline' ? (
        <button
          type="button"
          className="mermaid-diagram__canvas"
          onClick={() => openAdhocArtifact({ type: 'text/mermaid', title: t('render.diagram'), content: code })}
          title={t('render.openDiagram')}
        >
          {img}
        </button>
      ) : (
        <div className="mermaid-diagram__canvas mermaid-diagram__canvas--panel">{img}</div>
      );
  } else if (state.status === 'error' && !showCode) {
    body = (
      <>
        <div className="mermaid-diagram__error" role="note">
          <strong>{t('render.mermaidError')}</strong> {state.error}
        </div>
        {source}
      </>
    );
  } else if (showCode) {
    body = source;
  } else {
    body = (
      <>
        <div className="mermaid-diagram__note">
          {state.status === 'waiting' ? t('render.mermaidPending') : t('render.mermaidRendering')}
        </div>
        {source}
      </>
    );
  }

  if (variant === 'panel') return <div className="mermaid-diagram mermaid-diagram--panel">{body}</div>;

  return (
    <div className="code-block mermaid-diagram">
      <div className="code-block__header">
        <span className="code-block__lang">mermaid</span>
        <div className="code-block__actions">
          {state.status === 'ready' && (
            <button className="code-block__copy" type="button" onClick={() => setShowCode((v) => !v)} aria-pressed={showCode}>
              {showCode ? t('render.showDiagram') : t('render.showCode')}
            </button>
          )}
          <CopyButton text={code} />
        </div>
      </div>
      {body}
    </div>
  );
});
