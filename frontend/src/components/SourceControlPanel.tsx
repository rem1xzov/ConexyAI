import { useCallback, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  checkoutGitBranch,
  commitGit,
  getGitBranches,
  getGitLog,
  getGitStatus,
  stageGitPaths,
} from '../api/conexyApi';
import type { GitBranchInfo, GitCommitInfo, GitFileChange } from '../types/api';

// IDE_GIT: добавлено 2026-10-04 — панель Source Control (как в VS Code): ветка, изменённые файлы,
// staging по кнопке «+/−», поле коммита и история. Всё локально, без push.
interface SourceControlPanelProps {
  sessionId?: string;
  /** Refresh the file tree after git changes (checkout/commit can rewrite files on disk). */
  onChanged?: () => void;
  authorName?: string;
  authorEmail?: string;
  // IDE_DIFF: клик по файлу открывает сравнение «до/после».
  onOpenDiff?: (path: string) => void;
}

export function SourceControlPanel({ sessionId, onChanged, authorName, authorEmail, onOpenDiff }: SourceControlPanelProps) {
  const { t } = useTranslation();
  const [branch, setBranch] = useState<string | null>(null);
  const [isRepo, setIsRepo] = useState(true);
  const [changes, setChanges] = useState<GitFileChange[]>([]);
  const [branches, setBranches] = useState<GitBranchInfo[]>([]);
  const [commits, setCommits] = useState<GitCommitInfo[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    if (!sessionId) return;
    setLoading(true);
    setError(null);
    try {
      const status = await getGitStatus(sessionId);
      setIsRepo(status.isRepository);
      setBranch(status.branch ?? null);
      setChanges(status.changes ?? []);
      if (status.error) setError(status.error);

      if (status.isRepository) {
        const [log, branchList] = await Promise.all([getGitLog(sessionId, 30), getGitBranches(sessionId)]);
        setCommits(log.commits ?? []);
        setBranches(branchList.branches ?? []);
      } else {
        setCommits([]);
        setBranches([]);
      }
    } catch {
      setError(t('scm.error'));
    } finally {
      setLoading(false);
    }
  }, [sessionId, t]);

  useEffect(() => {
    void load();
  }, [load]);

  const staged = changes.filter((c) => c.staged);
  const unstaged = changes.filter((c) => !c.staged);
  const canCommit = staged.length > 0 && message.trim().length > 0 && !busy;

  async function toggle(path: string, toStaged: boolean) {
    if (!sessionId) return;
    setBusy(true);
    try {
      const res = await stageGitPaths(sessionId, [path], toStaged);
      if (!res.success) setError(res.error ?? t('scm.error'));
      await load();
    } finally {
      setBusy(false);
    }
  }

  async function toggleAll(toStaged: boolean) {
    if (!sessionId) return;
    setBusy(true);
    try {
      const res = await stageGitPaths(sessionId, [], toStaged);
      if (!res.success) setError(res.error ?? t('scm.error'));
      await load();
    } finally {
      setBusy(false);
    }
  }

  async function doCommit() {
    if (!sessionId || !canCommit) return;
    setBusy(true);
    setError(null);
    try {
      const res = await commitGit(sessionId, message.trim(), authorName, authorEmail);
      if (!res.success) {
        setError(res.error ?? t('scm.error'));
        return;
      }
      setMessage('');
      await load();
      onChanged?.();
    } finally {
      setBusy(false);
    }
  }

  async function doCheckout(name: string) {
    if (!sessionId || !name || name === branch) return;
    setBusy(true);
    setError(null);
    try {
      const res = await checkoutGitBranch(sessionId, name);
      if (!res.success) {
        setError(res.error ?? t('scm.error'));
        return;
      }
      await load();
      onChanged?.();
    } finally {
      setBusy(false);
    }
  }

  if (!sessionId) {
    return <div className="workspace__hint">{t('workspace.runTaskHint')}</div>;
  }

  return (
    <div className="scm">
      <div className="scm__toolbar">
        <select
          className="scm__branch"
          value={branch ?? ''}
          onChange={(e) => void doCheckout(e.target.value)}
          disabled={busy || !isRepo}
          title={t('scm.branch')}
        >
          {branch && !branches.some((b) => b.name === branch) && <option value={branch}>{branch}</option>}
          {branches.map((b) => (
            <option key={b.name} value={b.name}>
              {b.name}
              {b.isCurrent ? ' ✓' : ''}
            </option>
          ))}
        </select>
        <button
          className="scm__icon-btn"
          onClick={() => void load()}
          title={t('scm.refresh')}
          aria-label={t('scm.refresh')}
          disabled={loading}
          type="button"
        >
          ⟳
        </button>
      </div>

      {error && <div className="workspace__hint workspace__hint--error">{error}</div>}
      {!isRepo && <div className="workspace__hint">{t('scm.notARepo')}</div>}

      {isRepo && (
        <>
          <div className="scm__commit">
            <textarea
              className="dialog-input scm__message"
              rows={3}
              placeholder={t('scm.commitPlaceholder')}
              value={message}
              onChange={(e) => setMessage(e.target.value)}
            />
            <button
              className="dialog-btn dialog-btn--primary scm__commit-btn"
              disabled={!canCommit}
              onClick={() => void doCommit()}
              type="button"
            >
              {busy ? t('scm.working') : t('scm.commit')}
            </button>
          </div>

          {staged.length > 0 && (
            <div className="scm__group">
              <div className="scm__group-head">
                <span>
                  {t('scm.staged')} ({staged.length})
                </span>
                <button className="scm__link" onClick={() => void toggleAll(false)} disabled={busy} type="button">
                  {t('scm.unstageAll')}
                </button>
              </div>
              {staged.map((c) => (
                <ScmRow
                  key={c.path}
                  change={c}
                  onToggle={() => void toggle(c.path, false)}
                  onOpen={onOpenDiff ? () => onOpenDiff(c.path) : undefined}
                  label={t('scm.unstage')}
                  symbol="−"
                />
              ))}
            </div>
          )}

          <div className="scm__group">
            <div className="scm__group-head">
              <span>
                {t('scm.changes')} ({unstaged.length})
              </span>
              <button
                className="scm__link"
                onClick={() => void toggleAll(true)}
                disabled={busy || unstaged.length === 0}
                type="button"
              >
                {t('scm.stageAll')}
              </button>
            </div>
            {unstaged.length === 0 && <div className="workspace__hint">{t('scm.noChanges')}</div>}
            {unstaged.map((c) => (
              <ScmRow
                key={c.path}
                change={c}
                onToggle={() => void toggle(c.path, true)}
                onOpen={onOpenDiff ? () => onOpenDiff(c.path) : undefined}
                label={t('scm.stage')}
                symbol="+"
              />
            ))}
          </div>

          <div className="scm__group">
            <div className="scm__group-head">
              <span>{t('scm.history')}</span>
            </div>
            {commits.length === 0 && <div className="workspace__hint">{t('scm.noCommits')}</div>}
            {commits.map((c) => (
              <div className="scm__commit-row" key={c.hash} title={`${c.hash}\n${c.author} — ${c.date}`}>
                <span className="scm__hash">{c.shortHash}</span>
                <span className="scm__subject">{c.subject}</span>
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

function ScmRow({
  change,
  onToggle,
  onOpen,
  label,
  symbol,
}: {
  change: GitFileChange;
  onToggle: () => void;
  onOpen?: () => void;
  label: string;
  symbol: string;
}) {
  const { t } = useTranslation();
  return (
    <div className="scm__row" title={change.path}>
      <span className={`scm__badge scm__badge--${badgeClass(change.status)}`}>
        {change.status !== ' ' ? change.status : 'M'}
      </span>
      {onOpen ? (
        <button className="scm__path scm__path--button" onClick={onOpen} title={t('diff.open')} type="button">
          {change.path}
        </button>
      ) : (
        <span className="scm__path">{change.path}</span>
      )}
      <button className="scm__row-btn" onClick={onToggle} title={label} aria-label={label} type="button">
        {symbol}
      </button>
    </div>
  );
}

function badgeClass(status: string): string {
  switch (status) {
    case 'M':
      return 'modified';
    case 'A':
      return 'added';
    case 'D':
      return 'deleted';
    case 'R':
      return 'renamed';
    case '?':
      return 'untracked';
    default:
      return 'other';
  }
}
