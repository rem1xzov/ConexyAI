import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { getIdeSymbols } from '../api/conexyApi';
import type { IdeSymbolNode } from '../types/api';

// LSP_LITE: добавлено 2026-10-05 — панель Outline: дерево структуры текущего файла (классы, методы,
// функции, поля) с переходом к символу по клику. Символы разбирает бэкенд по тексту редактора.
interface OutlinePanelProps {
  sessionId?: string;
  /** Workspace-relative path of the file whose outline is shown. */
  path?: string | null;
  /** Live editor buffer, so unsaved edits are reflected. */
  content?: string;
  /** True while the Outline view is selected — refresh only then. */
  active: boolean;
  /** Open a symbol (used when clicking a row). */
  onOpenLocation: (path: string, line: number, column: number) => void;
}

export function OutlinePanel({ sessionId, path, content, active, onOpenLocation }: OutlinePanelProps) {
  const { t } = useTranslation();
  const [symbols, setSymbols] = useState<IdeSymbolNode[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const generation = useRef(0);

  const load = useCallback(async () => {
    if (!sessionId || !path) {
      setSymbols([]);
      return;
    }
    const current = ++generation.current;
    setLoading(true);
    setError(null);
    try {
      const result = await getIdeSymbols(sessionId, path, content);
      if (current !== generation.current) return;
      setSymbols(result.success ? result.symbols ?? [] : []);
      if (!result.success) setError(t('workspace.outlineFailed'));
    } catch {
      if (current === generation.current) {
        setSymbols([]);
        setError(t('workspace.outlineFailed'));
      }
    } finally {
      if (current === generation.current) setLoading(false);
    }
  }, [sessionId, path, content, t]);

  // Refresh on file/content change (debounced), only while the view is visible.
  useEffect(() => {
    if (!active) return;
    const timer = setTimeout(() => void load(), 350);
    return () => clearTimeout(timer);
  }, [active, load]);

  if (!sessionId || !path) {
    return <div className="workspace__hint">{t('workspace.outlineNoFile')}</div>;
  }

  return (
    <div className="outline">
      <div className="outline__toolbar">
        <span className="outline__file" title={path}>
          {path.split('/').pop()}
        </span>
        <button
          className="scm__icon-btn"
          onClick={() => void load()}
          disabled={loading}
          title={t('workspace.outlineRefresh')}
          aria-label={t('workspace.outlineRefresh')}
          type="button"
        >
          ⟳
        </button>
      </div>
      {error && <div className="workspace__hint workspace__hint--error">{error}</div>}
      {!error && !loading && symbols.length === 0 && (
        <div className="workspace__hint">{t('workspace.outlineEmpty')}</div>
      )}
      <div className="outline__tree">
        {symbols.map((symbol) => (
          <OutlineRow key={`${symbol.line}:${symbol.name}`} node={symbol} depth={0} path={path} onOpen={onOpenLocation} />
        ))}
      </div>
    </div>
  );
}

function OutlineRow({
  node,
  depth,
  path,
  onOpen,
}: {
  node: IdeSymbolNode;
  depth: number;
  path: string;
  onOpen: (path: string, line: number, column: number) => void;
}) {
  const [open, setOpen] = useState(true);
  const hasChildren = (node.children?.length ?? 0) > 0;
  const indent = { paddingLeft: `${depth * 14 + 8}px` };

  return (
    <>
      <div
        className="outline__row"
        style={indent}
        title={node.detail ?? node.name}
        onClick={() => onOpen(path, node.line, node.column)}
      >
        <button
          className={`outline__toggle ${hasChildren ? '' : 'outline__toggle--empty'}`}
          onClick={(e) => {
            e.stopPropagation();
            if (hasChildren) setOpen((v) => !v);
          }}
          tabIndex={hasChildren ? 0 : -1}
          aria-label={hasChildren ? (open ? 'collapse' : 'expand') : ''}
          type="button"
        >
          {hasChildren ? (open ? '▾' : '▸') : ''}
        </button>
        <span className={`outline__kind outline__kind--${node.kind}`}>{kindGlyph(node.kind)}</span>
        <span className="outline__name">{node.name}</span>
      </div>
      {hasChildren &&
        open &&
        node.children.map((child) => (
          <OutlineRow key={`${child.line}:${child.name}`} node={child} depth={depth + 1} path={path} onOpen={onOpen} />
        ))}
    </>
  );
}

function kindGlyph(kind: string): string {
  switch (kind) {
    case 'class':
      return 'C';
    case 'interface':
      return 'I';
    case 'struct':
      return 'S';
    case 'enum':
      return 'E';
    case 'module':
      return 'M';
    case 'type':
      return 'T';
    case 'method':
      return 'm';
    case 'function':
      return 'ƒ';
    case 'property':
      return 'P';
    case 'field':
      return 'F';
    default:
      return 'v';
  }
}
