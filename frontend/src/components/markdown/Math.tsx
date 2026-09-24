import { useEffect, useSyncExternalStore } from 'react';

/**
 * MATH_KATEX: добавлено 2026-09-24 — формулы через KaTeX.
 *
 * KaTeX (и его CSS со шрифтами) грузится лениво, отдельным чанком, при первой формуле на экране:
 * большинство ответов формул не содержит, и основной бандл не должен за это платить. До загрузки
 * формула показывается исходным TeX-ом.
 *
 * Это единственное место в рендере сообщений, где используется innerHTML (схемы mermaid идут
 * через <img>, всё остальное — текстовые узлы React): вывод `katex.renderToString` с
 * `trust: false` не пропускает \href/\url/\includegraphics/\htmlClass и прочие команды, способные
 * породить ссылки или атрибуты, а входной TeX экранируется самим KaTeX.
 */

type Katex = typeof import('katex').default;

let katex: Katex | null = null;
let loading: Promise<void> | null = null;
let failed = false;
const listeners = new Set<() => void>();

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function snapshot(): Katex | null {
  return katex;
}

function loadKatex(): void {
  if (katex || loading || failed) return;
  loading = Promise.all([import('katex'), import('katex/dist/katex.min.css')])
    .then(([mod]) => {
      katex = mod.default;
      listeners.forEach((listener) => listener());
    })
    .catch(() => {
      // Offline / chunk missing after a deploy: keep showing the TeX source.
      failed = true;
    })
    .finally(() => {
      loading = null;
    });
}

const cache = new Map<string, string>();
const CACHE_LIMIT = 500;

function renderTex(k: Katex, tex: string, display: boolean): string | null {
  const key = (display ? 'D' : 'I') + tex;
  const hit = cache.get(key);
  if (hit !== undefined) return hit;
  let html: string;
  try {
    html = k.renderToString(tex, {
      displayMode: display,
      throwOnError: false,
      trust: false,
      strict: 'ignore',
      output: 'htmlAndMathml',
      maxExpand: 500,
      maxSize: 50,
    });
  } catch {
    return null;
  }
  if (cache.size >= CACHE_LIMIT) cache.delete(cache.keys().next().value as string);
  cache.set(key, html);
  return html;
}

/**
 * `display` switches KaTeX to display style; `block` renders a <div> (a `$$…$$` paragraph of its
 * own). Display math met inside a sentence stays a <span> so it never nests a <div> in a <p>.
 */
export function MathTex({ tex, display, block = false }: { tex: string; display: boolean; block?: boolean }) {
  const k = useSyncExternalStore(subscribe, snapshot, snapshot);

  useEffect(() => {
    loadKatex();
  }, []);

  const html = k ? renderTex(k, tex, display) : null;
  const Tag = block ? 'div' : 'span';
  const className = block ? 'math-block' : display ? 'math-inline math-inline--display' : 'math-inline';

  if (html === null) {
    return <Tag className={`${className} math-source`}>{display ? tex : `$${tex}$`}</Tag>;
  }
  return <Tag className={className} dangerouslySetInnerHTML={{ __html: html }} />;
}
