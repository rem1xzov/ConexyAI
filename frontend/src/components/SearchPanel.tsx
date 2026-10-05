import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { replaceProject, searchProject } from '../api/conexyApi';
import type { SearchMatch } from '../types/api';

// SEARCH_REPLACE: добавлено 2026-10-05 — панель глобального поиска и замены по проекту (как Find &
// Replace в VS Code): строка или регулярное выражение, регистр, фильтр по glob, замена во всех
// файлах или в одном. Результаты сгруппированы по файлу, клик открывает совпадение.
interface SearchPanelProps {
  sessionId?: string;
  onOpenLocation: (path: string, line: number, column: number) => void;
  /** Called with the changed paths after a replace, so the editor can reload open files. */
  onReplaced: (paths: string[]) => void;
}

export function SearchPanel({ sessionId, onOpenLocation, onReplaced }: SearchPanelProps) {
  const { t } = useTranslation();
  const [query, setQuery] = useState('');
  const [replacement, setReplacement] = useState('');
  const [regex, setRegex] = useState(false);
  const [caseSensitive, setCaseSensitive] = useState(false);
  const [glob, setGlob] = useState('');
  const [matches, setMatches] = useState<SearchMatch[]>([]);
  const [total, setTotal] = useState(0);
  const [truncated, setTruncated] = useState(false);
  const [searched, setSearched] = useState(false);
  const [loading, setLoading] = useState(false);
  const [replacing, setReplacing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [armed, setArmed] = useState(false);

  const grouped = useMemo(() => groupByFile(matches), [matches]);

  async function runSearch(clearNotice = true) {
    if (!sessionId || query.length === 0) return;
    setLoading(true);
    setError(null);
    if (clearNotice) setNotice(null);
    setArmed(false);
    try {
      const res = await searchProject(sessionId, query, regex, caseSensitive, glob.trim() || undefined);
      setSearched(true);
      if (!res.success) {
        setMatches([]);
        setTotal(0);
        setError(errorText(res.error, t));
        return;
      }
      setMatches(res.matches ?? []);
      setTotal(res.totalMatches ?? 0);
      setTruncated(!!res.truncated);
    } catch {
      setError(t('search.failed'));
    } finally {
      setLoading(false);
    }
  }

  async function runReplace(path?: string) {
    if (!sessionId || query.length === 0) return;
    setReplacing(true);
    setError(null);
    setNotice(null);
    try {
      const res = await replaceProject(
        sessionId,
        query,
        replacement,
        regex,
        caseSensitive,
        glob.trim() || undefined,
        path,
      );
      if (!res.success) {
        setError(errorText(res.error, t));
        return;
      }
      setNotice(t('search.replaced', { count: res.replacements, files: res.filesChanged }));
      onReplaced((res.files ?? []).map((f) => f.path));
      await runSearch(false);
    } catch {
      setError(t('search.replaceFailed'));
    } finally {
      setReplacing(false);
      setArmed(false);
    }
  }

  const canSearch = !!sessionId && query.length > 0 && !loading;

  return (
    <div className="search">
      <div className="search__fields">
        <div className="search__row">
          <input
            className="dialog-input search__input"
            placeholder={t('search.find')}
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setArmed(false);
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter') void runSearch();
            }}
            spellCheck={false}
          />
          <button
            className={`search__toggle ${caseSensitive ? 'search__toggle--on' : ''}`}
            onClick={() => setCaseSensitive((v) => !v)}
            title={t('search.caseSensitive')}
            aria-label={t('search.caseSensitive')}
            type="button"
          >
            Aa
          </button>
          <button
            className={`search__toggle ${regex ? 'search__toggle--on' : ''}`}
            onClick={() => setRegex((v) => !v)}
            title={t('search.regex')}
            aria-label={t('search.regex')}
            type="button"
          >
            .*
          </button>
        </div>

        <div className="search__row">
          <input
            className="dialog-input search__input"
            placeholder={t('search.replace')}
            value={replacement}
            onChange={(e) => setReplacement(e.target.value)}
            spellCheck={false}
          />
          <button
            className="search__action"
            onClick={() => {
              if (armed) void runReplace();
              else setArmed(true);
            }}
            disabled={!canSearch || replacing}
            title={t('search.replaceAllHint')}
            type="button"
          >
            {armed ? t('search.replaceAllConfirm', { count: total }) : t('search.replaceAll')}
          </button>
        </div>

        <input
          className="dialog-input search__glob"
          placeholder={t('search.includeGlob')}
          value={glob}
          onChange={(e) => setGlob(e.target.value)}
          spellCheck={false}
        />
      </div>

      {error && <div className="workspace__hint workspace__hint--error">{error}</div>}
      {notice && <div className="workspace__hint">{notice}</div>}
      {truncated && <div className="workspace__hint">{t('search.truncated')}</div>}

      {searched && !error && matches.length === 0 && !loading && (
        <div className="workspace__hint">{t('search.noResults')}</div>
      )}
      {matches.length > 0 && (
        <div className="search__summary">{t('search.summary', { count: total, files: grouped.length })}</div>
      )}

      <div className="search__results">
        {grouped.map(([file, items]) => (
          <div key={file} className="search__file">
            <div className="search__file-head">
              <span className="search__file-name" title={file}>
                {file.split('/').pop()}
                <span className="search__file-count">{items.length}</span>
              </span>
              <button
                className="search__file-replace"
                onClick={() => void runReplace(file)}
                disabled={replacing}
                title={t('search.replaceInFile')}
                type="button"
              >
                ⇄
              </button>
            </div>
            {items.map((m, i) => {
              const { before, hit, after } = previewParts(m);
              return (
                <button
                  key={`${file}:${m.line}:${m.column}:${i}`}
                  className="search__match"
                  onClick={() => onOpenLocation(m.path, m.line, m.column)}
                  title={`${m.path}:${m.line}:${m.column}`}
                  type="button"
                >
                  <span className="search__line">{m.line}</span>
                  <span className="search__preview">
                    {before}
                    <mark className="search__hit">{hit}</mark>
                    {after}
                  </span>
                </button>
              );
            })}
          </div>
        ))}
      </div>
    </div>
  );
}

function groupByFile(matches: SearchMatch[]): Array<[string, SearchMatch[]]> {
  const map = new Map<string, SearchMatch[]>();
  for (const match of matches) {
    const list = map.get(match.path);
    if (list) list.push(match);
    else map.set(match.path, [match]);
  }
  return Array.from(map.entries());
}

function previewParts(match: SearchMatch): { before: string; hit: string; after: string } {
  const start = Math.max(0, match.column - 1);
  const end = Math.min(match.preview.length, start + match.length);
  return {
    before: match.preview.slice(0, start),
    hit: match.preview.slice(start, end),
    after: match.preview.slice(end),
  };
}

function errorText(code: string | null | undefined, t: (key: string) => string): string {
  switch (code) {
    case 'EMPTY_QUERY':
      return t('search.errEmpty');
    case 'INVALID_PATTERN':
      return t('search.errPattern');
    case 'QUERY_TOO_LONG':
    case 'REPLACEMENT_TOO_LONG':
      return t('search.errTooLong');
    case 'NO_WORKSPACE':
      return t('search.errNoWorkspace');
    case 'INVALID_PATH':
    case 'FILE_NOT_FOUND':
      return t('search.errNotFound');
    default:
      return t('search.failed');
  }
}
