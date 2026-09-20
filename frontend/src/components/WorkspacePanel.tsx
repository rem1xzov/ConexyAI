import { useEffect, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import {
  createIdeFile,
  deleteWorkspaceFile,
  downloadWorkspaceZip,
  getIdeFileContent,
  getWorkspaceFiles,
  saveIdeFileContent,
  uploadWorkspaceZip,
} from '../api/conexyApi';
import type { WorkspaceFileEntry, WorkspaceListing } from '../types/api';
import type { TodoItem, ToolActionEvent } from '../types/signalr';
import { signalrService } from '../services/signalrService';
import { changedLineNumbers } from '../utils/diff';
import { CodeEditor } from './CodeEditor';
import { ToolActionFeed } from './ToolActionFeed';
import { TodoPanel } from './TodoPanel';
import { FileTypeIcon } from './FileTypeIcon';
import { Breadcrumbs } from './Breadcrumbs';
import { CommandPalette } from './CommandPalette';
import { ConfirmDialog, PromptDialog } from './Dialog';
import { MenuBar, type Menu } from './MenuBar';
import {
  CheckIcon,
  CloseIcon,
  CodeIcon,
  DownloadIcon,
  PlayIcon,
  RefreshIcon,
  TrashIcon,
} from './Icons';

interface WorkspacePanelProps {
  sessionId?: string;
  /** Materializes a draft agent chat (so a workspace chatId exists) before file operations. */
  onEnsureWorkspace?: () => string;
  running: boolean;
  fileCreatedEvent?: { path: string; name: string } | null;
  fileRefreshToken?: number;
  agentFileChange?: { path: string } | null;
  toolActions?: ToolActionEvent[];
  todos?: TodoItem[];
  style?: CSSProperties;
  onCursorChange?: (pos: { line: number; column: number; language: string }) => void;
  onRunInSeparateWindow?: () => void;
}

interface OpenTab {
  path: string;
  name: string;
  content: string;
  savedContent: string;
  isBinary: boolean;
}

type SaveStatus = 'idle' | 'saving' | 'saved' | 'error';
type BottomTab = 'status' | 'todo';

interface PendingConflict {
  path: string;
  incomingContent: string;
}

type DialogState =
  | { kind: 'confirm'; title: string; message?: string; confirmLabel?: string; danger?: boolean; onConfirm: () => void }
  | { kind: 'prompt'; title: string; message?: string; placeholder?: string; onSubmit: (value: string) => void }
  | null;

function fileName(path: string): string {
  const parts = path.split('/');
  return parts[parts.length - 1] || path;
}

function triggerDownload(blob: Blob, fileNameValue: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileNameValue;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

/** Branded empty state for the Monaco editor area (right pane) when no file is open. */
function EditorEmptyState({ title, text }: { title: string; text: string }) {
  return (
    <div className="editor-empty">
      <pre className="editor-empty__code" aria-hidden="true">
        {'namespace ConexyAI;\n\nclass Project\n{\n    // your code will appear here\n}'}
      </pre>
      <div className="editor-empty__content">
        <div className="editor-empty__glyph">{'</>'}</div>
        <div className="editor-empty__title">{title}</div>
        <div className="editor-empty__text">{text}</div>
      </div>
    </div>
  );
}

interface TreeNodeProps {
  node: WorkspaceFileEntry;
  depth: number;
  activePath: string | null;
  dirtyPaths?: Set<string>;
  onOpenFile: (path: string) => void;
  onDelete: (path: string) => void;
}

function TreeNode({ node, depth, activePath, dirtyPaths, onOpenFile, onDelete }: TreeNodeProps) {
  const [open, setOpen] = useState(depth === 0);
  const indent = { paddingLeft: `${depth * 14 + 8}px` };

  if (node.isDirectory) {
    return (
      <div>
        <button className="file-tree__row" style={indent} onClick={() => setOpen((o) => !o)} title={node.path}>
          <span className={`file-tree__chevron ${open ? 'file-tree__chevron--open' : ''}`}>▸</span>
          <FileTypeIcon path={node.path} isFolder size={14} />
          <span className="file-tree__name">{node.name}</span>
        </button>
        {open &&
          node.children.map((child) => (
            <TreeNode
              key={child.path}
              node={child}
              depth={depth + 1}
              activePath={activePath}
              dirtyPaths={dirtyPaths}
              onOpenFile={onOpenFile}
              onDelete={onDelete}
            />
          ))}
      </div>
    );
  }

  return (
    <div
      className={`file-tree__row file-tree__row--file ${activePath === node.path ? 'file-tree__row--active' : ''}`}
      style={indent}
      onClick={() => onOpenFile(node.path)}
      title={node.path}
    >
      <span className="file-tree__chevron file-tree__chevron--spacer" />
      <FileTypeIcon path={node.path} size={14} />
      <span className="file-tree__name">{node.name}</span>
      {dirtyPaths?.has(node.path) && <span className="file-tree__dirty" title="Unsaved changes">M</span>}
      <button
        className="file-tree__action"
        title={`Delete ${node.name}`}
        aria-label={`Delete ${node.name}`}
        onClick={(e) => {
          e.stopPropagation();
          onDelete(node.path);
        }}
      >
        <TrashIcon size={13} />
      </button>
    </div>
  );
}

export function WorkspacePanel({
  sessionId,
  onEnsureWorkspace,
  running,
  fileCreatedEvent,
  fileRefreshToken,
  agentFileChange,
  toolActions = [],
  todos = [],
  style,
  onCursorChange,
  onRunInSeparateWindow,
}: WorkspacePanelProps) {
  const { t } = useTranslation();
  const [listing, setListing] = useState<WorkspaceListing | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [tabs, setTabs] = useState<OpenTab[]>([]);
  const [activePath, setActivePath] = useState<string | null>(null);

  // Default to 'status' (not 'terminal') so selecting a chat never auto-mounts the
  // TerminalPanel — which would spawn the server-side pty (a visible window on Windows).
  const [bottomTab, setBottomTab] = useState<BottomTab>('status');
  const [saveStatus, setSaveStatus] = useState<SaveStatus>('idle');
  const [conflict, setConflict] = useState<PendingConflict | null>(null);
  const [zipping, setZipping] = useState(false);
  const [runBusy, setRunBusy] = useState(false);
  const [runError, setRunError] = useState<string | null>(null);
  const [highlight, setHighlight] = useState<{ lines: number[]; nonce: number } | null>(null);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [isWindows, setIsWindows] = useState(false);
  const [dialog, setDialog] = useState<DialogState>(null);
  const [explorerVisible, setExplorerVisible] = useState(true);
  const [toast, setToast] = useState<string | null>(null);

  const tabsRef = useRef<OpenTab[]>([]);
  const toastTimer = useRef<number | null>(null);
  const zipInputRef = useRef<HTMLInputElement>(null);

  function notify(message: string) {
    setToast(message);
    if (toastTimer.current) window.clearTimeout(toastTimer.current);
    toastTimer.current = window.setTimeout(() => setToast(null), 2600);
  }

  useEffect(() => {
    tabsRef.current = tabs;
  }, [tabs]);

  // Monotonic token used to ignore out-of-order file-tree responses when the user
  // switches between agent chats quickly (otherwise a slow response from the previous
  // chat can overwrite the new chat's listing).
  const loadSeqRef = useRef(0);

  // Detect the backend host OS once so Run can adapt (Windows => visible console window).
  useEffect(() => {
    void signalrService
      .getPlatform()
      .then((p) => setIsWindows(p === 'windows'))
      .catch(() => {
        // Default to the Linux/pty behavior if the platform can't be determined.
      });
  }, []);

  const activeTab = tabs.find((t) => t.path === activePath) ?? null;
  const hasFiles = (listing?.files.length ?? 0) > 0;
  const dirtyPaths = new Set(tabs.filter((t) => t.content !== t.savedContent).map((t) => t.path));

  async function loadFiles() {
    if (!sessionId) return;
    const seq = ++loadSeqRef.current;
    setLoading(true);
    setError(null);
    try {
      const data = await getWorkspaceFiles(sessionId);
      if (seq !== loadSeqRef.current) return; // stale response from a previous chat
      setListing(data);
    } catch (e) {
      if (seq !== loadSeqRef.current) return;
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      if (seq === loadSeqRef.current) setLoading(false);
    }
  }

  useEffect(() => {
    // Switching chats must immediately drop the previous chat's tree and any open
    // tabs, then fetch the new chat's files from scratch — never showing stale files.
    setActivePath(null);
    setTabs([]);
    setConflict(null);
    setBottomTab('status');
    setDialog(null);
    setListing(null);
    setError(null);
    void loadFiles();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId]);

  async function openFile(path: string) {
    if (!sessionId) return;
    const existing = tabsRef.current.find((t) => t.path === path);
    if (existing) {
      setActivePath(path);
      return;
    }

    try {
      const res = await getIdeFileContent(sessionId, path);
      setTabs((prev) => [
        ...prev,
        { path, name: fileName(path), content: res.content, savedContent: res.content, isBinary: res.isBinary },
      ]);
      setActivePath(path);
      setSaveStatus('idle');
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  async function doCreateFile(path: string) {
    // Materialize the draft chat on first file creation so the workspace (and its chatId)
    // exists on disk and in the sidebar list before the agent ever runs.
    const id = onEnsureWorkspace?.() ?? sessionId;
    if (!id) return;
    try {
      await createIdeFile(id, path, false);
      await loadFiles();
      await openFile(path);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  function promptCreateFile() {
    setDialog({
      kind: 'prompt',
      title: t('workspace.newFile'),
      placeholder: 'src/app.cs',
      onSubmit: (value) => void doCreateFile(value),
    });
  }

  function handleEditorChange(path: string, value: string) {
    setTabs((prev) => prev.map((t) => (t.path === path ? { ...t, content: value } : t)));
    setSaveStatus('idle');
  }

  async function saveTab(path: string) {
    if (!sessionId) return;
    const tab = tabsRef.current.find((t) => t.path === path);
    if (!tab || tab.isBinary) return;

    setSaveStatus('saving');
    try {
      await saveIdeFileContent(sessionId, tab.path, tab.content);
      setTabs((prev) => prev.map((t) => (t.path === path ? { ...t, savedContent: t.content } : t)));
      setSaveStatus('saved');
    } catch {
      setSaveStatus('error');
    }
  }

  function removeTab(path: string) {
    const remaining = tabsRef.current.filter((t) => t.path !== path);
    setTabs(remaining);
    if (activePath === path) {
      setActivePath(remaining.length > 0 ? remaining[remaining.length - 1].path : null);
    }
    if (conflict?.path === path) setConflict(null);
  }

  function closeTab(path: string) {
    const tab = tabsRef.current.find((t) => t.path === path);
    if (tab && tab.content !== tab.savedContent) {
      setDialog({
        kind: 'confirm',
        title: t('workspace.unsavedChanges'),
        message: t('workspace.unsavedChangesMsg'),
        confirmLabel: t('workspace.closeWithoutSave'),
        danger: true,
        onConfirm: () => removeTab(path),
      });
      return;
    }
    removeTab(path);
  }

  async function deleteFile(path: string) {
    if (!sessionId) return;
    try {
      await deleteWorkspaceFile(sessionId, path);
      removeTab(path);
      await loadFiles();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  async function downloadZip() {
    if (!sessionId) return;
    setZipping(true);
    try {
      const blob = await downloadWorkspaceZip(sessionId);
      triggerDownload(blob, 'workspace.zip');
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setZipping(false);
    }
  }

  async function handleZipUpload(file: File) {
    if (file.size > 50 * 1024 * 1024) {
      notify(t('workspace.fileTooLarge'));
      return;
    }
    const id = onEnsureWorkspace?.() ?? sessionId;
    if (!id) return;
    notify(t('workspace.uploading'));
    try {
      await uploadWorkspaceZip(id, file);
      notify(t('workspace.uploaded'));
      await loadFiles();
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e);
      notify(t('workspace.uploadError', { message }));
      setError(message);
    }
  }

  async function handleRun() {
    if (!sessionId || runBusy) return;
    setRunBusy(true);
    setRunError(null);
    try {
      // Subscribe to the task stream before running so its output/status events are
      // received. On Windows the run opens a separate visible console window instead.
      if (!isWindows) {
        await new Promise((resolve) => setTimeout(resolve, 100));
        await signalrService.joinTask(sessionId);
      }

      const res = await signalrService.runProject(sessionId);

      if (res.needsManualConfig) {
        setDialog({
          kind: 'prompt',
          title: t('workspace.runCommandTitle'),
          message: t('workspace.runCommandMsg'),
          placeholder: 'dotnet run',
          onSubmit: (cmd) => {
            void (async () => {
              const res2 = await signalrService.runProjectWithCommand(sessionId, cmd);
              if (res2.launchedInSeparateWindow) onRunInSeparateWindow?.();
              else if (res2.error) setRunError(res2.error);
            })();
          },
        });
      } else if (res.error) {
        setRunError(res.error);
      } else if (res.launchedInSeparateWindow) {
        onRunInSeparateWindow?.();
      }
    } catch (e) {
      setRunError(e instanceof Error ? e.message : String(e));
    } finally {
      setRunBusy(false);
    }
  }

  // Keep a stable reference so the Ctrl+F5 listener always invokes the latest handler.
  const runHandlerRef = useRef<() => void>(() => {});
  useEffect(() => {
    runHandlerRef.current = handleRun;
  });

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.ctrlKey && e.key === 'F5') {
        e.preventDefault();
        runHandlerRef.current();
        return;
      }
      const paletteKey = e.ctrlKey && (e.key === 'k' || e.key === 'K' || (e.shiftKey && (e.key === 'P' || e.key === 'p')));
      if (paletteKey) {
        e.preventDefault();
        setPaletteOpen((o) => !o);
      }
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  // Refresh the tree when the agent mutates files (create / str_replace / insert).
  useEffect(() => {
    if (fileRefreshToken && fileRefreshToken > 0) {
      void loadFiles();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fileRefreshToken, sessionId]);

  // Instantly open a file the agent just created.
  useEffect(() => {
    if (fileCreatedEvent?.path) {
      void loadFiles();
      void openFile(fileCreatedEvent.path);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fileCreatedEvent]);

  // Agent edited a file: refresh the open tab's content, but never silently clobber
  // a user's unsaved edits — surface a conflict prompt instead.
  useEffect(() => {
    if (!agentFileChange?.path || !sessionId) return;

    void (async () => {
      const tab = tabsRef.current.find((t) => t.path === agentFileChange.path);
      if (!tab) return;
      try {
        const res = await getIdeFileContent(sessionId, agentFileChange.path);
        const isDirty = tab.content !== tab.savedContent;
        if (isDirty) {
          setConflict({ path: agentFileChange.path, incomingContent: res.content });
        } else {
          // Diff old vs new content and flash the touched lines, but only when the
          // change is actually applied (conflicts show a prompt instead, no highlight).
          const lines = changedLineNumbers(tab.content, res.content);
          setTabs((prev) =>
            prev.map((t) =>
              t.path === agentFileChange.path
                ? { ...t, content: res.content, savedContent: res.content, isBinary: res.isBinary }
                : t,
            ),
          );
          if (lines.length > 0) setHighlight({ lines, nonce: Date.now() });
        }
      } catch {
        // The file may have been deleted; the next tree refresh will reconcile.
      }
    })();
  }, [agentFileChange, sessionId]);

  function applyConflict() {
    if (!conflict) return;
    setTabs((prev) =>
      prev.map((t) =>
        t.path === conflict.path ? { ...t, content: conflict.incomingContent, savedContent: conflict.incomingContent } : t,
      ),
    );
    setConflict(null);
  }

  const menus: Menu[] = [
    {
      label: t('workspace.menuFile'),
      items: [
        {
          label: t('workspace.menuNewFile'),
          action: () => promptCreateFile(),
        },
        {
          label: t('workspace.menuOpenZip'),
          action: () => zipInputRef.current?.click(),
        },
        {
          label: t('workspace.menuSave'),
          shortcut: 'Ctrl+S',
          action: () => {
            if (!activePath) {
              notify(t('workspace.noFileToSave'));
              return;
            }
            void saveTab(activePath);
          },
        },
        { separator: true },
        {
          label: t('workspace.menuDownloadZip'),
          action: () => {
            if (!hasFiles) {
              notify(t('workspace.emptyDownload'));
              return;
            }
            void downloadZip();
          },
        },
      ],
    },
    {
      label: t('workspace.menuView'),
      items: [
        { label: t('workspace.menuToggleExplorer'), action: () => setExplorerVisible((v) => !v) },
      ],
    },
  ];

  return (
    <div className="workspace" style={style}>
      <MenuBar menus={menus} />
      <input
        ref={zipInputRef}
        type="file"
        accept=".zip"
        hidden
        onChange={(e) => {
          const file = e.target.files?.[0];
          if (file) void handleZipUpload(file);
          e.target.value = '';
        }}
      />
      <header className="workspace__header">
        <div className="workspace__title">
          <span>{t('workspace.title')}</span>
          {sessionId && <span className="workspace__id">{sessionId.slice(0, 8)}…</span>}
        </div>
        <div className="workspace__actions">
          <button
            className="workspace__run-btn"
            onClick={() => void handleRun()}
            disabled={!sessionId || runBusy}
            title={t('workspace.runTitle')}
            aria-label={t('workspace.runAria')}
          >
            <PlayIcon size={15} />
            {runBusy ? t('workspace.running') : t('workspace.run')}
          </button>
          {runError && <span className="workspace__run-error">{runError}</span>}
          {activeTab && (
            <span className={`workspace__save-status workspace__save-status--${saveStatus}`}>
              {saveStatus === 'saving' && t('workspace.saving')}
              {saveStatus === 'saved' && t('workspace.saved')}
              {saveStatus === 'error' && t('workspace.saveError')}
              {saveStatus === 'idle' && (activeTab.content !== activeTab.savedContent ? t('workspace.notSaved') : '')}
            </span>
          )}
          <button className="icon-btn" onClick={() => void loadFiles()} title={t('workspace.refreshFiles')} aria-label={t('workspace.refreshFiles')}>
            <RefreshIcon size={16} />
          </button>
          <button
            className="workspace__zip-btn"
            onClick={() => void downloadZip()}
            disabled={!sessionId || !hasFiles || zipping}
            title={t('workspace.downloadZipTitle')}
          >
            <DownloadIcon size={15} />
            {zipping ? t('workspace.packaging') : t('workspace.downloadZip')}
          </button>
        </div>
      </header>

      <div className="workspace__body">
        {explorerVisible && (
        <aside className="workspace__explorer">
          <div className="workspace__explorer-label">{t('workspace.fileExplorer')}</div>
          {!sessionId && <div className="workspace__hint">{t('workspace.runTaskHint')}</div>}
          {loading && !listing && <div className="workspace__hint">{t('common.loading')}</div>}
          {error && <div className="workspace__hint workspace__hint--error">{error}</div>}
          {!loading && !error && listing && !hasFiles && <div className="workspace__hint">{t('workspace.noFiles')}</div>}
          {listing && hasFiles && (
            <div className="file-tree">
              {listing.tree.map((node) => (
                <TreeNode
                  key={node.path}
                  node={node}
                  depth={0}
                  activePath={activePath}
                  dirtyPaths={dirtyPaths}
                  onOpenFile={(p) => void openFile(p)}
                  onDelete={(p) => void deleteFile(p)}
                />
              ))}
            </div>
          )}
        </aside>
        )}

        <section className="workspace__viewer">
          <div className="workspace__tabs">
            {tabs.map((tab) => (
              <div
                key={tab.path}
                className={`workspace__tab ${tab.path === activePath ? 'workspace__tab--active' : ''}`}
                onClick={() => setActivePath(tab.path)}
                title={tab.path}
              >
                <FileTypeIcon path={tab.path} size={14} />
                <span className="workspace__tab-name">{tab.name}</span>
                {tab.content !== tab.savedContent && <span className="workspace__tab-dirty">●</span>}
                <button
                  className="workspace__tab-close"
                  onClick={(e) => {
                    e.stopPropagation();
                    closeTab(tab.path);
                  }}
                  title={t('workspace.closeTab')}
                  aria-label={t('workspace.closeTabAria', { name: tab.name })}
                >
                  <CloseIcon size={12} />
                </button>
              </div>
            ))}
            {tabs.length === 0 && <div className="workspace__tabs-empty">{t('workspace.noOpenFiles')}</div>}
          </div>

          {activeTab && !activeTab.isBinary && (
            <Breadcrumbs path={activeTab.path} files={listing?.files ?? []} onOpenFile={(p) => void openFile(p)} />
          )}

          {conflict && (
            <div className="workspace__conflict">
              <span className="workspace__conflict-text">{t('workspace.fileChanged')}</span>
              <button className="workspace__conflict-btn" onClick={applyConflict}>
                {t('workspace.update')}
              </button>
              <button className="workspace__conflict-btn workspace__conflict-btn--secondary" onClick={() => setConflict(null)}>
                {t('workspace.keepMine')}
              </button>
            </div>
          )}

          <div className="workspace__editor-body">
            {activeTab ? (
              activeTab.isBinary ? (
                <div className="workspace__hint workspace__hint--center">{t('workspace.binaryFile')}</div>
              ) : (
                <CodeEditor
                  path={activeTab.path}
                  content={activeTab.content}
                  onChange={(v) => handleEditorChange(activeTab.path, v)}
                  onSave={() => void saveTab(activeTab.path)}
                  highlight={highlight}
                  onCursorChange={onCursorChange}
                />
              )
            ) : hasFiles ? (
              <EditorEmptyState title={t('workspace.selectFileTitle')} text={t('workspace.selectFileText')} />
            ) : sessionId ? (
              <div className="workspace__empty">
                <div className="workspace__empty-icon">📂</div>
                <div className="workspace__empty-title">{t('workspace.emptyTitle')}</div>
                <div className="workspace__empty-text">
                  {t('workspace.emptyText')}
                </div>
                <button className="workspace__empty-btn" onClick={promptCreateFile}>
                  {t('workspace.createFile')}
                </button>
                <div className="workspace__empty-hints">
                  <span>
                    <kbd className="workspace__empty-kbd">Ctrl K</kbd> {t('workspace.commands')}
                  </span>
                  <span>
                    <kbd className="workspace__empty-kbd">Ctrl F5</kbd> {t('workspace.launch')}
                  </span>
                </div>
              </div>
            ) : (
              <EditorEmptyState title={t('workspace.generateTitle')} text={t('workspace.generateText')} />
            )}
          </div>
        </section>
      </div>

      <div className="workspace__bottom">
        <div className="workspace__bottom-tabs">
          <button
            className={`workspace__bottom-tab ${bottomTab === 'status' ? 'workspace__bottom-tab--active' : ''}`}
            onClick={() => setBottomTab('status')}
          >
            <CodeIcon size={14} /> {t('workspace.status')}
          </button>
          <button
            className={`workspace__bottom-tab ${bottomTab === 'todo' ? 'workspace__bottom-tab--active' : ''}`}
            onClick={() => setBottomTab('todo')}
          >
            <CheckIcon size={14} /> {t('workspace.todo')}
          </button>
        </div>
        <div className="workspace__bottom-body">
          {bottomTab === 'status' &&
            (toolActions.length > 0 ? (
              <ToolActionFeed actions={toolActions} />
            ) : (
              <div className="workspace__empty workspace__empty--panel">
                <div className="workspace__empty-text">{t('workspace.statusEmpty')}</div>
              </div>
            ))}
          {bottomTab === 'todo' &&
            (todos.length > 0 ? (
              <TodoPanel todos={todos} />
            ) : (
              <div className="workspace__empty workspace__empty--panel">
                <div className="workspace__empty-text">{t('workspace.todoEmpty')}</div>
              </div>
            ))}
        </div>
      </div>

      <CommandPalette
        open={paletteOpen}
        files={listing?.files ?? []}
        onClose={() => setPaletteOpen(false)}
        onOpenFile={(p) => void openFile(p)}
        onRun={() => void handleRun()}
        onToggleTodo={() => setBottomTab('todo')}
        onSave={() => activePath && void saveTab(activePath)}
      />

      {dialog?.kind === 'confirm' && (
        <ConfirmDialog
          title={dialog.title}
          message={dialog.message}
          confirmLabel={dialog.confirmLabel}
          danger={dialog.danger}
          onConfirm={() => {
            dialog.onConfirm();
            setDialog(null);
          }}
          onCancel={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'prompt' && (
        <PromptDialog
          title={dialog.title}
          message={dialog.message}
          placeholder={dialog.placeholder}
          onSubmit={(value) => {
            dialog.onSubmit(value);
            setDialog(null);
          }}
          onCancel={() => setDialog(null)}
        />
      )}

      {toast && <div className="workspace__toast">{toast}</div>}
    </div>
  );
}
