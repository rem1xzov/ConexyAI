import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import {
  createIdeFile,
  deleteWorkspaceFile,
  downloadWorkspaceRaw,
  downloadWorkspaceZip,
  getIdeFileContent,
  getWorkspaceFiles,
  saveIdeFileContent,
  uploadWorkspaceZip,
} from '../api/conexyApi';
import http from '../api/client';
import type { WorkspaceFileEntry, WorkspaceListing } from '../types/api';
import type { TodoItem } from '../types/signalr';
import { signalrService } from '../services/signalrService';
import { changedLineNumbers } from '../utils/diff';
import { humanError } from '../utils/humanError';
import { triggerDownload } from '../utils/download';
import { CodeEditor, disposeEditorModels, editorModelUri } from './CodeEditor';
import { TodoPanel } from './TodoPanel';
import { FileTypeIcon } from './FileTypeIcon';
import { Breadcrumbs } from './Breadcrumbs';
import { CommandPalette } from './CommandPalette';
import { ConfirmDialog, PromptDialog } from './Dialog';
import { MenuBar, type Menu } from './MenuBar';
import {
  CheckIcon,
  CloseIcon,
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
  // AGENT_FEED_ZED: добавлено 2026-09-23
  /** Opens a path the user clicked in the agent feed. The nonce makes a repeat click on the same
   *  path re-trigger the effect (an equal object would be swallowed by React's dependency check). */
  openFileRequest?: { path: string; nonce: number } | null;
  fileRefreshToken?: number;
  agentFileChange?: { path: string } | null;
  todos?: TodoItem[];
  style?: CSSProperties;
  onCursorChange?: (pos: { line: number; column: number; language: string }) => void;
  onRunInSeparateWindow?: () => void;
  // COWORK_MODE: Cowork produces documents, not programs — there is nothing to "Run".
  hideRun?: boolean;
  // WORKSPACE_RACES: добавлено 2026-09-24 — сообщает, есть ли в открытом чате несохранённые файлы,
  // чтобы родитель мог спросить подтверждение ДО переключения чата. Без него панель сама спрашивает
  // сразу после переключения (правки к этому моменту уже отложены в памяти, ничего не теряется).
  onDirtyChange?: (dirty: boolean) => void;
}

interface OpenTab {
  path: string;
  name: string;
  content: string;
  /** What we believe is on disk: the text last loaded from or saved to the server. */
  savedContent: string;
  isBinary: boolean;
  /** Bumped on every local edit, so a finished save can tell whether newer edits exist. */
  version: number;
  /** Bumped when the text is replaced from outside the editor (agent edit, "take the agent's version"). */
  rev: number;
}

type SaveStatus = 'idle' | 'saving' | 'saved' | 'error';
// STATUS_TAB_REMOVED: добавлено 2026-09-22 — вкладка «Статус» удалена целиком: она дублировала
// ленту чата (те же команды и карточки подтверждения). В нижней панели осталась только «Задачи».
type BottomTab = 'todo';

type DialogState =
  | { kind: 'confirm'; title: string; message?: string; confirmLabel?: string; danger?: boolean; onConfirm: () => void }
  | { kind: 'prompt'; title: string; message?: string; placeholder?: string; onSubmit: (value: string) => void }
  | null;

function fileName(path: string): string {
  const parts = path.split('/');
  return parts[parts.length - 1] || path;
}

function isDirty(tab: OpenTab): boolean {
  return !tab.isBinary && tab.content !== tab.savedContent;
}

function underPath(candidate: string, path: string, isDirectory: boolean): boolean {
  return candidate === path || (isDirectory && candidate.startsWith(`${path.replace(/\/+$/, '')}/`));
}

// ---------------------------------------------------------------------------------------------
// WORKSPACE_RACES: добавлено 2026-09-24 (M14) — вкладки чата, из которого ушли, не выбрасываются:
// они откладываются здесь (в памяти страницы) и возвращаются, когда пользователь снова открывает
// этот чат. Несохранённые правки при уходе вызывают вопрос «Сохранить / Не сохранять / Позже».
// Хранилище модульное, потому что панель размонтируется при переходе в обычный чат.
// ---------------------------------------------------------------------------------------------

interface StashedTabs {
  tabs: OpenTab[];
  activePath: string | null;
  /** Ask "save / discard / later" about this chat's unsaved edits. */
  needsPrompt: boolean;
  at: number;
}

const tabStash = new Map<string, StashedTabs>();
const STASH_CLEAN_LIMIT = 20;

function stashTabs(chatId: string, tabs: OpenTab[], activePath: string | null): void {
  if (tabs.length === 0) {
    tabStash.delete(chatId);
    return;
  }
  tabStash.set(chatId, { tabs, activePath, needsPrompt: tabs.some(isDirty), at: Date.now() });
  // Bound memory: forget the oldest chats whose tabs hold nothing unsaved.
  const clean = [...tabStash.entries()].filter(([, e]) => !e.tabs.some(isDirty)).sort((a, b) => a[1].at - b[1].at);
  while (clean.length > STASH_CLEAN_LIMIT) {
    const [id] = clean.shift()!;
    tabStash.delete(id);
  }
}

function hasUnsavedAnywhere(currentTabs: OpenTab[]): boolean {
  if (currentTabs.some(isDirty)) return true;
  for (const entry of tabStash.values()) if (entry.tabs.some(isDirty)) return true;
  return false;
}

// The workspace-file endpoint of conexyApi removes files only; the IDE endpoint also removes a
// folder with everything inside it.
async function deleteWorkspaceFolder(chatId: string, path: string): Promise<void> {
  await http.delete(`/sessions/${chatId}/files`, { params: { path } });
}

// ---------------------------------------------------------------------------------------------

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
  onDelete: (path: string, isDirectory: boolean) => void;
}

function TreeNode({ node, depth, activePath, dirtyPaths, onOpenFile, onDelete }: TreeNodeProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(depth === 0);
  const indent = { paddingLeft: `${depth * 14 + 8}px` };

  const deleteButton = (
    <button
      className="file-tree__action file-tree__action--danger"
      title={t('workspace.deleteItem', { name: node.name })}
      aria-label={t('workspace.deleteItem', { name: node.name })}
      onClick={(e) => {
        e.stopPropagation();
        onDelete(node.path, node.isDirectory);
      }}
    >
      <TrashIcon size={13} />
    </button>
  );

  if (node.isDirectory) {
    return (
      <div>
        <div
          className="file-tree__row file-tree__row--folder"
          style={indent}
          onClick={() => setOpen((o) => !o)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' || e.key === ' ') {
              e.preventDefault();
              setOpen((o) => !o);
            }
          }}
          role="button"
          tabIndex={0}
          aria-expanded={open}
          title={node.path}
        >
          <span className={`file-tree__chevron ${open ? 'file-tree__chevron--open' : ''}`}>▸</span>
          <FileTypeIcon path={node.path} isFolder size={14} />
          <span className="file-tree__name">{node.name}</span>
          {deleteButton}
        </div>
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
      {dirtyPaths?.has(node.path) && (
        <span className="file-tree__dirty" title={t('workspace.unsavedChanges')}>
          M
        </span>
      )}
      {deleteButton}
    </div>
  );
}

export function WorkspacePanel({
  sessionId,
  onEnsureWorkspace,
  running,
  fileCreatedEvent,
  openFileRequest,
  fileRefreshToken,
  agentFileChange,
  todos = [],
  style,
  onCursorChange,
  onRunInSeparateWindow,
  hideRun,
  onDirtyChange,
}: WorkspacePanelProps) {
  const { t } = useTranslation();
  const [listing, setListing] = useState<WorkspaceListing | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [tabs, setTabs] = useState<OpenTab[]>([]);
  const [activePath, setActivePath] = useState<string | null>(null);

  // STATUS_TAB_REMOVED: добавлено 2026-09-22 — единственная вкладка нижней панели.
  const [bottomTab, setBottomTab] = useState<BottomTab>('todo');
  // Save state and agent conflicts are per file, so switching tabs never shows another file's state.
  const [saveStatus, setSaveStatus] = useState<Record<string, SaveStatus>>({});
  /** path -> the agent's version of a file the user was editing (waiting for "keep mine / take agent's"). */
  const [conflicts, setConflicts] = useState<Record<string, string>>({});
  const [zipping, setZipping] = useState(false);
  const [runBusy, setRunBusy] = useState(false);
  const [runError, setRunError] = useState<string | null>(null);
  const [highlight, setHighlight] = useState<{ path: string; lines: number[]; nonce: number } | null>(null);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [isWindows, setIsWindows] = useState(false);
  const [dialog, setDialog] = useState<DialogState>(null);
  const [leavePrompt, setLeavePrompt] = useState<{ chatId: string; files: string[] } | null>(null);
  const [leaveBusy, setLeaveBusy] = useState(false);
  const [explorerVisible, setExplorerVisible] = useState(true);
  const [toast, setToast] = useState<string | null>(null);

  const toastTimer = useRef<number | null>(null);
  const zipInputRef = useRef<HTMLInputElement>(null);

  // WORKSPACE_RACES: добавлено 2026-09-24 (M14).
  // `tabsRef` — единственный источник правды о вкладках, и обновляется СИНХРОННО (updateTabs):
  // решение «файл грязный → конфликт, чистый → подменить» принимается по актуальным данным, а не
  // по снимку до await, из-за которого правка агента затирала только что набранный текст.
  const tabsRef = useRef<OpenTab[]>([]);
  const activePathRef = useRef<string | null>(null);
  // Чат, который показан сейчас. Каждый асинхронный запрос запоминает свой чат и после await
  // сверяется с ним: ответ, начатый в чате A, никогда не попадает во вкладки чата B.
  const chatIdRef = useRef<string | undefined>(sessionId);
  const prevChatRef = useRef<string | undefined>(undefined);
  // Monotonic token used to ignore out-of-order file-tree responses.
  const loadSeqRef = useRef(0);
  const openSeqRef = useRef(0);
  const saveSeqRef = useRef(new Map<string, number>());
  const reloadSeqRef = useRef(new Map<string, number>());
  const mountedRef = useRef(false);

  useLayoutEffect(() => {
    chatIdRef.current = sessionId;
  }, [sessionId]);

  useLayoutEffect(() => {
    activePathRef.current = activePath;
  }, [activePath]);

  const updateTabs = useCallback((fn: (prev: OpenTab[]) => OpenTab[]) => {
    const next = fn(tabsRef.current);
    if (next === tabsRef.current) return;
    tabsRef.current = next;
    setTabs(next);
  }, []);

  function isCurrentChat(chatId: string | undefined): chatId is string {
    return !!chatId && chatIdRef.current === chatId;
  }

  /**
   * Disposes a chat's editor models (all of them, or those of the given paths) on the next tick —
   * after the editor let go of them — unless by then the panel shows that chat (and those files)
   * again, e.g. after React's StrictMode remount or a quick A→B→A switch.
   */
  function disposeModelsLater(chatId: string, paths?: string[]) {
    window.setTimeout(() => {
      const showing = mountedRef.current && chatIdRef.current === chatId;
      if (!paths) {
        if (!showing) disposeEditorModels(chatId);
        return;
      }
      for (const path of paths) {
        const reopened = showing && tabsRef.current.some((tab) => tab.path === path);
        if (!reopened) disposeEditorModels(chatId, path);
      }
    }, 0);
  }

  function notify(message: string) {
    setToast(message);
    if (toastTimer.current) window.clearTimeout(toastTimer.current);
    toastTimer.current = window.setTimeout(() => setToast(null), 2600);
  }

  function setPathStatus(path: string, status: SaveStatus) {
    setSaveStatus((s) => (s[path] === status ? s : { ...s, [path]: status }));
  }

  function clearConflict(path: string) {
    setConflicts((c) => {
      if (!(path in c)) return c;
      const next = { ...c };
      delete next[path];
      return next;
    });
  }

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
  const dirtyPaths = new Set(tabs.filter(isDirty).map((t) => t.path));
  const hasDirtyTabs = dirtyPaths.size > 0;
  const activeConflict = activePath ? conflicts[activePath] : undefined;

  useEffect(() => {
    onDirtyChange?.(hasDirtyTabs);
  }, [hasDirtyTabs, onDirtyChange]);

  // Closing or reloading the page with unsaved edits (here or parked for another chat) asks first.
  useEffect(() => {
    function onBeforeUnload(e: BeforeUnloadEvent) {
      if (!hasUnsavedAnywhere(tabsRef.current)) return;
      e.preventDefault();
      e.returnValue = '';
    }
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, []);

  async function loadFiles(chatId: string | undefined = chatIdRef.current) {
    if (!chatId) return;
    const seq = ++loadSeqRef.current;
    setLoading(true);
    setError(null);
    try {
      const data = await getWorkspaceFiles(chatId);
      if (seq !== loadSeqRef.current || !isCurrentChat(chatId)) return; // stale response
      setListing(data);
    } catch (e) {
      if (seq !== loadSeqRef.current || !isCurrentChat(chatId)) return;
      setError(humanError(e, t));
    } finally {
      if (seq === loadSeqRef.current) setLoading(false);
    }
  }

  /**
   * Re-reads an open file after someone else (the agent, a ZIP upload) may have changed it on disk.
   * A clean tab takes the new text; a tab with unsaved edits is never overwritten — the user gets a
   * "keep mine / take the agent's" choice instead.
   */
  async function reloadFromDisk(path: string, flash = true) {
    const chatId = chatIdRef.current;
    if (!chatId || !tabsRef.current.some((tab) => tab.path === path)) return;
    const seq = (reloadSeqRef.current.get(path) ?? 0) + 1;
    reloadSeqRef.current.set(path, seq);

    let res: Awaited<ReturnType<typeof getIdeFileContent>>;
    try {
      res = await getIdeFileContent(chatId, path);
    } catch {
      return; // The file may have been deleted; the next tree refresh reconciles.
    }
    if (!isCurrentChat(chatId) || reloadSeqRef.current.get(path) !== seq) return;

    // Decide on the CURRENT tab state (tabsRef is updated synchronously), not on a snapshot taken
    // before the request: the user may have typed while it was in flight.
    const tab = tabsRef.current.find((x) => x.path === path);
    if (!tab) return;
    if (res.content === tab.savedContent && res.isBinary === tab.isBinary) return; // nothing new on disk

    if (isDirty(tab)) {
      if (res.content === tab.content) {
        // The disk now holds exactly the user's text — nothing to resolve.
        updateTabs((prev) => prev.map((x) => (x.path === path ? { ...x, savedContent: res.content } : x)));
        clearConflict(path);
        return;
      }
      setConflicts((c) => ({ ...c, [path]: res.content }));
      return;
    }

    const lines = changedLineNumbers(tab.content, res.content);
    updateTabs((prev) =>
      prev.map((x) =>
        x.path === path
          ? { ...x, content: res.content, savedContent: res.content, isBinary: res.isBinary, version: x.version + 1, rev: x.rev + 1 }
          : x,
      ),
    );
    clearConflict(path);
    // Flash the touched lines only in the file that is on screen.
    if (flash && lines.length > 0 && activePathRef.current === path) {
      setHighlight({ path, lines, nonce: Date.now() });
    }
  }

  useEffect(() => {
    const prev = prevChatRef.current;
    prevChatRef.current = sessionId;

    // Park the chat we are leaving (its tabs come back when the user returns to it).
    if (prev && prev !== sessionId) {
      stashTabs(prev, tabsRef.current, activePathRef.current);
      // Its editor models go: restored tabs recreate them from the parked text.
      disposeModelsLater(prev);
    }

    // Switching chats must immediately drop the previous chat's tree and tabs, then fetch the new
    // chat's files from scratch — never showing (or saving into) the wrong chat.
    const restored = sessionId ? tabStash.get(sessionId) : undefined;
    if (sessionId) tabStash.delete(sessionId);
    const nextTabs = restored?.tabs ?? [];
    tabsRef.current = nextTabs;
    setTabs(nextTabs);
    const nextActive = restored ? (restored.activePath ?? nextTabs[0]?.path ?? null) : null;
    activePathRef.current = nextActive;
    setActivePath(nextActive);
    setConflicts({});
    setSaveStatus({});
    setHighlight(null);
    setDialog(null);
    setListing(null);
    setError(null);
    setRunError(null);
    setBottomTab('todo');
    void loadFiles(sessionId);

    // Restored tabs may be stale: the agent could have changed those files meanwhile.
    for (const tab of nextTabs) if (!tab.isBinary) void reloadFromDisk(tab.path, false);

    // Ask about unsaved edits left behind in another chat.
    const pending = [...tabStash.entries()].find(([id, e]) => id !== sessionId && e.needsPrompt && e.tabs.some(isDirty));
    setLeavePrompt(pending ? { chatId: pending[0], files: pending[1].tabs.filter(isDirty).map((x) => x.path) } : null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId]);

  // The panel itself goes away (IDE hidden, switched to a plain chat): park the tabs the same way.
  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      const chatId = chatIdRef.current;
      if (!chatId) return;
      stashTabs(chatId, tabsRef.current, activePathRef.current);
      disposeModelsLater(chatId);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function nextLeavePrompt() {
    const current = chatIdRef.current;
    const pending = [...tabStash.entries()].find(([id, e]) => id !== current && e.needsPrompt && e.tabs.some(isDirty));
    setLeavePrompt(pending ? { chatId: pending[0], files: pending[1].tabs.filter(isDirty).map((x) => x.path) } : null);
  }

  function keepLeftEditsForLater(chatId: string) {
    const entry = tabStash.get(chatId);
    if (entry) entry.needsPrompt = false;
    nextLeavePrompt();
  }

  function discardLeftEdits(chatId: string) {
    const entry = tabStash.get(chatId);
    if (entry) {
      entry.tabs = entry.tabs.map((tab) =>
        isDirty(tab) ? { ...tab, content: tab.savedContent, version: tab.version + 1, rev: tab.rev + 1 } : tab,
      );
      entry.needsPrompt = false;
    }
    nextLeavePrompt();
  }

  async function saveLeftEdits(chatId: string) {
    const entry = tabStash.get(chatId);
    if (!entry) {
      nextLeavePrompt();
      return;
    }
    setLeaveBusy(true);
    const dirty = entry.tabs.filter(isDirty);
    const results = await Promise.allSettled(
      dirty.map(async (tab) => {
        await saveIdeFileContent(chatId, tab.path, tab.content);
        return { path: tab.path, sent: tab.content };
      }),
    );
    const saved = new Map<string, string>();
    let failed = 0;
    for (const r of results) {
      if (r.status === 'fulfilled') saved.set(r.value.path, r.value.sent);
      else failed += 1;
    }
    const markSaved = (list: OpenTab[]) =>
      list.map((tab) => (saved.has(tab.path) ? { ...tab, savedContent: saved.get(tab.path)! } : tab));
    // The tabs may still be parked, or already restored because the user went back meanwhile.
    const parked = tabStash.get(chatId);
    if (parked) {
      parked.tabs = markSaved(parked.tabs);
      parked.needsPrompt = false;
    }
    if (isCurrentChat(chatId)) updateTabs(markSaved);
    setLeaveBusy(false);
    notify(failed > 0 ? t('workspace.leaveSaveFailed', { count: failed }) : t('workspace.leaveSaved', { count: saved.size }));
    nextLeavePrompt();
  }

  async function openFile(path: string) {
    const chatId = chatIdRef.current;
    if (!chatId) return;
    const request = ++openSeqRef.current;
    if (tabsRef.current.some((tab) => tab.path === path)) {
      setActivePath(path);
      return;
    }

    try {
      const res = await getIdeFileContent(chatId, path);
      // The user switched chats meanwhile: this file belongs to the other chat's workspace.
      if (!isCurrentChat(chatId)) return;
      // Two opens of the same path can race (agent event + click); keep a single tab.
      if (!tabsRef.current.some((tab) => tab.path === path)) {
        updateTabs((prev) => [
          ...prev,
          {
            path,
            name: fileName(path),
            content: res.content,
            savedContent: res.content,
            isBinary: res.isBinary,
            version: 0,
            rev: 0,
          },
        ]);
      }
      // Only the most recent open request steals focus.
      if (request === openSeqRef.current) setActivePath(path);
    } catch (e) {
      if (isCurrentChat(chatId)) setError(humanError(e, t));
    }
  }

  async function doCreateFile(path: string) {
    // Materialize the draft chat on first file creation so the workspace (and its chatId)
    // exists on disk and in the sidebar list before the agent ever runs.
    const id = onEnsureWorkspace?.() ?? chatIdRef.current;
    if (!id) return;
    try {
      await createIdeFile(id, path, false);
      if (!isCurrentChat(id)) return;
      await loadFiles(id);
      await openFile(path);
    } catch (e) {
      if (isCurrentChat(id)) setError(humanError(e, t));
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
    let changed = false;
    updateTabs((prev) =>
      prev.map((tab) => {
        if (tab.path !== path || tab.content === value) return tab;
        changed = true;
        return { ...tab, content: value, version: tab.version + 1 };
      }),
    );
    if (changed) setPathStatus(path, 'idle');
  }

  async function saveTab(path: string) {
    const chatId = chatIdRef.current;
    const tab = tabsRef.current.find((x) => x.path === path);
    if (!chatId || !tab || tab.isBinary) return;

    // Exactly this snapshot is sent, and only this snapshot is marked as saved afterwards: text
    // typed while the request is in flight stays "unsaved" instead of being silently lost.
    const snapshot = tab.content;
    const version = tab.version;
    // Keyed by chat too: two chats can both have a file at this path (chat ids never contain "/").
    const saveKey = `${chatId}/${path}`;
    const seq = (saveSeqRef.current.get(saveKey) ?? 0) + 1;
    saveSeqRef.current.set(saveKey, seq);

    setPathStatus(path, 'saving');
    try {
      await saveIdeFileContent(chatId, path, snapshot);
      if (!isCurrentChat(chatId)) {
        // The user left the chat while saving: its tabs are parked — record the save there, so the
        // "unsaved changes" question does not ask about text that is already on disk.
        const parked = tabStash.get(chatId);
        if (parked && saveSeqRef.current.get(saveKey) === seq) {
          parked.tabs = parked.tabs.map((x) => (x.path === path ? { ...x, savedContent: snapshot } : x));
          if (!parked.tabs.some(isDirty)) {
            parked.needsPrompt = false;
            nextLeavePrompt();
          }
        }
        return;
      }
      if (saveSeqRef.current.get(saveKey) !== seq) return; // a newer save of this file owns the status
      updateTabs((prev) => prev.map((x) => (x.path === path ? { ...x, savedContent: snapshot } : x)));
      // Saving over a pending agent change means "keep mine".
      clearConflict(path);
      const current = tabsRef.current.find((x) => x.path === path);
      setPathStatus(path, current && current.version === version ? 'saved' : 'idle');
    } catch {
      if (isCurrentChat(chatId) && saveSeqRef.current.get(saveKey) === seq) setPathStatus(path, 'error');
    }
  }

  function removeTabs(match: (path: string) => boolean) {
    const chatId = chatIdRef.current;
    const removed = tabsRef.current.filter((tab) => match(tab.path));
    if (removed.length === 0) return;
    const remaining = tabsRef.current.filter((tab) => !match(tab.path));
    updateTabs(() => remaining);
    if (activePathRef.current && match(activePathRef.current)) {
      setActivePath(remaining.length > 0 ? remaining[remaining.length - 1].path : null);
    }
    setConflicts((c) => {
      const next = { ...c };
      for (const tab of removed) delete next[tab.path];
      return next;
    });
    // Drop the editor models too, so reopening shows fresh text with a fresh undo history.
    if (chatId) disposeModelsLater(chatId, removed.map((tab) => tab.path));
  }

  function closeTab(path: string) {
    const tab = tabsRef.current.find((x) => x.path === path);
    if (tab && isDirty(tab)) {
      setDialog({
        kind: 'confirm',
        title: t('workspace.unsavedChanges'),
        message: t('workspace.unsavedChangesMsg'),
        confirmLabel: t('workspace.closeWithoutSave'),
        danger: true,
        onConfirm: () => removeTabs((p) => p === path),
      });
      return;
    }
    removeTabs((p) => p === path);
  }

  // CONFIRM_DIALOGS: добавлено 2026-09-24 (M21) — удаление из дерева файлов необратимо, поэтому
  // сначала подтверждение. Чат запоминается в момент вопроса: ответ «Удалить» относится к нему.
  function requestDelete(path: string, isDirectory: boolean) {
    const chatId = chatIdRef.current;
    if (!chatId) return;
    const name = fileName(path.replace(/\/+$/, ''));
    const loosesEdits = tabsRef.current.some((tab) => underPath(tab.path, path, isDirectory) && isDirty(tab));
    const message =
      t(isDirectory ? 'workspace.deleteFolderMessage' : 'workspace.deleteFileMessage', { path }) +
      (loosesEdits ? ` ${t('workspace.deleteUnsavedNote')}` : '');
    setDialog({
      kind: 'confirm',
      title: t(isDirectory ? 'workspace.deleteFolderTitle' : 'workspace.deleteFileTitle', { name }),
      message,
      confirmLabel: t('workspace.delete'),
      danger: true,
      onConfirm: () => void deleteEntry(chatId, path, isDirectory),
    });
  }

  async function deleteEntry(chatId: string, path: string, isDirectory: boolean) {
    try {
      if (isDirectory) await deleteWorkspaceFolder(chatId, path);
      else await deleteWorkspaceFile(chatId, path);
      if (!isCurrentChat(chatId)) return;
      removeTabs((p) => underPath(p, path, isDirectory));
      await loadFiles(chatId);
    } catch (e) {
      if (isCurrentChat(chatId)) setError(humanError(e, t));
    }
  }

  async function downloadZip() {
    const chatId = chatIdRef.current;
    if (!chatId) return;
    setZipping(true);
    try {
      const blob = await downloadWorkspaceZip(chatId);
      triggerDownload(blob, 'workspace.zip');
    } catch (e) {
      if (isCurrentChat(chatId)) setError(humanError(e, t));
    } finally {
      setZipping(false);
    }
  }

  async function handleZipUpload(file: File) {
    if (file.size > 50 * 1024 * 1024) {
      notify(t('workspace.fileTooLarge'));
      return;
    }
    const id = onEnsureWorkspace?.() ?? chatIdRef.current;
    if (!id) return;
    notify(t('workspace.uploading'));
    try {
      await uploadWorkspaceZip(id, file);
      // The archive went into the chat it was started in; only that chat's view is refreshed.
      if (!isCurrentChat(id)) return;
      notify(t('workspace.uploaded'));
      await loadFiles(id);
      // The archive may have replaced files that are open right now.
      for (const tab of tabsRef.current) if (!tab.isBinary) void reloadFromDisk(tab.path);
    } catch (e) {
      if (!isCurrentChat(id)) return;
      const message = humanError(e, t);
      notify(t('workspace.uploadError', { message }));
      setError(message);
    }
  }

  async function handleRun() {
    // Ctrl+F5 and the command palette reach this too, not only the (hidden) button.
    const chatId = chatIdRef.current;
    if (!chatId || runBusy || hideRun) return;
    setRunBusy(true);
    setRunError(null);
    try {
      // Subscribe to the task stream before running so its output/status events are
      // received. On Windows the run opens a separate visible console window instead.
      if (!isWindows) {
        await new Promise((resolve) => setTimeout(resolve, 100));
        await signalrService.joinTask(chatId);
      }

      const res = await signalrService.runProject(chatId);
      if (!isCurrentChat(chatId)) return;

      if (res.needsManualConfig) {
        setDialog({
          kind: 'prompt',
          title: t('workspace.runCommandTitle'),
          message: t('workspace.runCommandMsg'),
          placeholder: 'dotnet run',
          onSubmit: (cmd) => {
            void (async () => {
              try {
                const res2 = await signalrService.runProjectWithCommand(chatId, cmd);
                if (res2.launchedInSeparateWindow) onRunInSeparateWindow?.();
                else if (res2.error && isCurrentChat(chatId)) setRunError(res2.error);
              } catch (e) {
                if (isCurrentChat(chatId)) setRunError(humanError(e, t));
              }
            })();
          },
        });
      } else if (res.error) {
        setRunError(res.error);
        // RUN_CRASH: the toolbar truncates the error to one short line; the refusal explains what
        // to do instead, so it is shown in full once.
        notify(res.error);
      } else if (res.launchedInSeparateWindow) {
        onRunInSeparateWindow?.();
      }
    } catch (e) {
      if (isCurrentChat(chatId)) setRunError(humanError(e, t));
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
    // Deliberately not keyed on sessionId: the chat switch loads the tree itself.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fileRefreshToken]);

  // Instantly open a file the agent just created (or refresh it, if it is already open).
  useEffect(() => {
    if (fileCreatedEvent?.path) {
      const path = fileCreatedEvent.path;
      void loadFiles();
      if (tabsRef.current.some((tab) => tab.path === path)) {
        setActivePath(path);
        void reloadFromDisk(path);
      } else {
        void openFile(path);
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fileCreatedEvent]);

  // AGENT_FEED_ZED: открыть путь, по которому кликнули в ленте шагов агента.
  useEffect(() => {
    if (openFileRequest?.path) void openFile(openFileRequest.path);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [openFileRequest]);

  // Agent edited a file: refresh the open tab's content, but never silently clobber the user's
  // unsaved edits — surface a conflict instead. Not keyed on sessionId: the previous chat's last
  // change must not be replayed against the next chat's files.
  useEffect(() => {
    if (agentFileChange?.path) void reloadFromDisk(agentFileChange.path);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [agentFileChange]);

  function takeAgentVersion(path: string) {
    const incoming = conflicts[path];
    if (incoming === undefined) return;
    updateTabs((prev) =>
      prev.map((x) =>
        x.path === path ? { ...x, content: incoming, savedContent: incoming, version: x.version + 1, rev: x.rev + 1 } : x,
      ),
    );
    clearConflict(path);
    setPathStatus(path, 'idle');
  }

  function keepMyVersion(path: string) {
    const incoming = conflicts[path];
    if (incoming === undefined) return;
    // The disk now holds the agent's text; the user's text stays in the editor as unsaved changes,
    // and a save will deliberately write it over the agent's version.
    updateTabs((prev) => prev.map((x) => (x.path === path ? { ...x, savedContent: incoming } : x)));
    clearConflict(path);
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

  const activeSaveStatus: SaveStatus = (activePath && saveStatus[activePath]) || 'idle';

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
          {!hideRun && (
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
          )}
          {!hideRun && runError && <span className="workspace__run-error" title={runError}>{runError}</span>}
          {activeTab && (
            <span className={`workspace__save-status workspace__save-status--${activeSaveStatus}`}>
              {activeSaveStatus === 'saving' && t('workspace.saving')}
              {activeSaveStatus === 'saved' && t('workspace.saved')}
              {activeSaveStatus === 'error' && t('workspace.saveError')}
              {activeSaveStatus === 'idle' && (isDirty(activeTab) ? t('workspace.notSaved') : '')}
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
                  onDelete={requestDelete}
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
                title={conflicts[tab.path] !== undefined ? t('workspace.conflictBadge') : tab.path}
              >
                <FileTypeIcon path={tab.path} size={14} />
                <span className="workspace__tab-name">{tab.name}</span>
                {conflicts[tab.path] !== undefined && <span className="workspace__tab-conflict">!</span>}
                {isDirty(tab) && <span className="workspace__tab-dirty">●</span>}
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

          {activeTab && activeConflict !== undefined && (
            <div className="workspace__conflict" role="alert">
              <span className="workspace__conflict-text">{t('workspace.fileChanged', { name: activeTab.name })}</span>
              <button className="workspace__conflict-btn" onClick={() => takeAgentVersion(activeTab.path)}>
                {t('workspace.update')}
              </button>
              <button
                className="workspace__conflict-btn workspace__conflict-btn--secondary"
                onClick={() => keepMyVersion(activeTab.path)}
              >
                {t('workspace.keepMine')}
              </button>
            </div>
          )}

          <div className="workspace__editor-body">
            {activeTab ? (
              activeTab.isBinary ? (
                <div className="workspace__hint workspace__hint--center">
                  {t('workspace.binaryFile')}
                  {/* OFFICE_FORMATS: documents the agent creates are binary — let them be downloaded. */}
                  {sessionId && (
                    <button
                      type="button"
                      className="workspace__conflict-btn"
                      onClick={() => {
                        void downloadWorkspaceRaw(sessionId, activeTab.path)
                          .then((blob) => triggerDownload(blob, fileName(activeTab.path)))
                          .catch((e: unknown) => notify(humanError(e, t)));
                      }}
                    >
                      {t('workspace.downloadFile')}
                    </button>
                  )}
                </div>
              ) : (
                <CodeEditor
                  path={activeTab.path}
                  modelPath={sessionId ? editorModelUri(sessionId, activeTab.path) : undefined}
                  content={activeTab.content}
                  revision={activeTab.rev}
                  onChange={(v) => handleEditorChange(activeTab.path, v)}
                  onSave={() => void saveTab(activeTab.path)}
                  highlight={highlight && highlight.path === activeTab.path ? highlight : null}
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
            className={`workspace__bottom-tab ${bottomTab === 'todo' ? 'workspace__bottom-tab--active' : ''}`}
            onClick={() => setBottomTab('todo')}
          >
            <CheckIcon size={14} /> {t('workspace.todo')}
          </button>
        </div>
        <div className="workspace__bottom-body">
          {todos.length > 0 ? (
            <TodoPanel todos={todos} />
          ) : (
            <div className="workspace__empty workspace__empty--panel">
              <div className="workspace__empty-text">{t('workspace.todoEmpty')}</div>
            </div>
          )}
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

      {leavePrompt && !dialog && (
        <ConfirmDialog
          title={t('workspace.leaveUnsavedTitle')}
          message={t('workspace.leaveUnsavedMessage', {
            count: leavePrompt.files.length,
            files: leavePrompt.files.slice(0, 3).map(fileName).join(', ') + (leavePrompt.files.length > 3 ? ', …' : ''),
          })}
          confirmLabel={t('workspace.leaveSave')}
          cancelLabel={t('workspace.leaveKeep')}
          extraAction={{ label: t('workspace.leaveDiscard'), danger: true, onClick: () => discardLeftEdits(leavePrompt.chatId) }}
          busy={leaveBusy}
          onConfirm={() => void saveLeftEdits(leavePrompt.chatId)}
          onCancel={() => keepLeftEditsForLater(leavePrompt.chatId)}
        />
      )}

      {toast && <div className="workspace__toast">{toast}</div>}
    </div>
  );
}
