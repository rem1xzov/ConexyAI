import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { setAuthToken } from './api/client';
import { getSubscriptionUsage, runTask } from './api/conexyApi';
import { signalrService } from './services/signalrService';
import { useAuth } from './hooks/useAuth';
import { useIsMobile } from './hooks/useMediaQuery';
import { Sidebar } from './components/Sidebar';
import { ChatFeed } from './components/ChatFeed';
import { InputBar } from './components/InputBar';
import { ModelPicker } from './components/ModelPicker';
import { WorkspacePanel } from './components/WorkspacePanel';
import { StatusBar } from './components/StatusBar';
// LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
// import { LiveVoiceModal } from './components/LiveVoiceModal';
import { MenuIcon, PanelRightCloseIcon, PanelRightOpenIcon } from './components/Icons';
// SUBSCRIPTION_TIERS: добавлено 2026-09-17
import { UsageIndicator } from './components/UsageIndicator';
import { UpgradeModal } from './components/UpgradeModal';
// ADMIN_PANEL: добавлено 2026-09-19
import { AdminPanel } from './components/AdminPanel';
// SUPPORT: добавлено 2026-09-19
import { SupportChat } from './components/SupportChat';
// EMAIL_AUTH: добавлено 2026-09-19
import { AuthModal } from './components/AuthModal';
import type { AuthMode } from './components/AuthModal';
import { SettingsModal } from './components/SettingsModal';
import { getStoredTheme, setTheme, type Theme } from './theme';
import { setLanguage } from './i18n';
import type { ConexyModel, LimitExceededInfo, ReasoningEffort, SubscriptionUsage, TaskAttachment } from './types/api';
import type { ChatMessage, ChatSession, ChatSessionKind } from './types/chat';
import type { CommandApproval, PendingActionPayload } from './types/signalr';

const STORAGE_KEY = 'conexy_sessions';

function uid(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`;
}

function normalizeMessage(m: ChatMessage): ChatMessage {
  return {
    ...m,
    thinking: m.thinking ?? '',
    logs: m.logs ?? [],
    screenshots: m.screenshots ?? [],
    toolActions: m.toolActions ?? [],
    todos: m.todos ?? [],
    problems: m.problems ?? [],
  };
}

function normalizeSession(s: ChatSession): ChatSession {
  const raw = s.model as string;
  const model: ConexyModel =
    raw === 'conexy-coder' || raw === 'Conexy-coder' ? 'conexy-coder'
    : raw === 'ConexyV1-pro' ? 'ConexyV1-pro'
    : 'ConexyV1-flash';
  return {
    ...s,
    model,
    kind: s.kind ?? (model === 'conexy-coder' ? 'projects' : 'chat'),
    messages: (s.messages ?? []).map(normalizeMessage),
  };
}

function loadSessions(): ChatSession[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as ChatSession[];
    return Array.isArray(parsed) ? parsed.map(normalizeSession) : [];
  } catch {
    return [];
  }
}

function saveSessions(sessions: ChatSession[]): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(sessions));
  } catch {
    // ignore quota / availability errors
  }
}

function updateMessage(
  sessions: ChatSession[],
  sessionId: string,
  messageId: string,
  updater: (m: ChatMessage) => ChatMessage,
): ChatSession[] {
  return sessions.map((s) =>
    s.id !== sessionId
      ? s
      : { ...s, messages: s.messages.map((m) => (m.id !== messageId ? m : updater(m))) },
  );
}

function updateSession(
  sessions: ChatSession[],
  sessionId: string,
  updater: (s: ChatSession) => ChatSession,
): ChatSession[] {
  return sessions.map((s) => (s.id !== sessionId ? s : updater(s)));
}

function defaultTitle(kind: ChatSessionKind, t: (key: string) => string): string {
  if (kind === 'students') return t('sidebar.newStudentSession');
  if (kind === 'projects') return t('sidebar.newAgentTask');
  return t('sidebar.newChat');
}

export default function App() {
  // EMAIL_AUTH: добавлено 2026-09-19
  const { token, user, initializing, error: authError, login, register, logout } = useAuth();
  // SETTINGS: добавлено 2026-09-19
  const { t, i18n } = useTranslation();
  // MOBILE: добавлено 2026-09-19 — drives the responsive layout (sidebar overlay, agent tabs).
  const isMobile = useIsMobile();

  const [activeTab, setActiveTab] = useState<ChatSessionKind>('chat');
  const [model, setModel] = useState<ConexyModel>('ConexyV1-flash');
  const [thinking, setThinking] = useState(false);
  const [reasoningEffort, setReasoningEffort] = useState<ReasoningEffort>('high');
  const [smartSearch, setSmartSearch] = useState(false);
  const [search, setSearch] = useState('');
  const [sidebarOpen, setSidebarOpen] = useState(() => !isMobile);
  const [workspaceWidth, setWorkspaceWidth] = useState(55); // % width of the IDE pane
  const [ideCollapsed, setIdeCollapsed] = useState(false);

  const [sessions, setSessions] = useState<ChatSession[]>(loadSessions);
  // Every visit starts on a fresh, empty chat with the default model instead of restoring
  // the last opened session. Previous chats stay available from the sidebar.
  const [activeId, setActiveId] = useState<string | null>(() => uid());
  const [toast, setToast] = useState<string | null>(null);
  const [fileCreatedEvent, setFileCreatedEvent] = useState<{ path: string; name: string } | null>(null);
  const [fileRefreshToken, setFileRefreshToken] = useState(0);
  const [agentFileChange, setAgentFileChange] = useState<{ path: string } | null>(null);
  const [agentStatus, setAgentStatus] = useState('Ready');
  const [cursorInfo, setCursorInfo] = useState<{ line: number; column: number; language: string } | null>(null);
  // COMMAND_CONFIRM: добавлено 2026-09-20
  // The command awaiting a decision, and the task (if any) whose commands run without asking.
  const [pendingAction, setPendingAction] = useState<PendingActionPayload | null>(null);
  const [allowAllTaskId, setAllowAllTaskId] = useState<string | null>(null);
  // SUBSCRIPTION_TIERS: добавлено 2026-09-17
  const [usage, setUsage] = useState<SubscriptionUsage | null>(null);
  const [limitExceeded, setLimitExceeded] = useState<LimitExceededInfo | null>(null);
  // EMAIL_AUTH: добавлено 2026-09-19
  const [authModal, setAuthModal] = useState<AuthMode | null>(null);
  // ADMIN_PANEL: добавлено 2026-09-19
  const [upgradeOpen, setUpgradeOpen] = useState(false);
  const [route, setRoute] = useState(window.location.hash);
  // SUPPORT: добавлено 2026-09-19
  const [supportOpen, setSupportOpen] = useState(false);
  // SETTINGS: добавлено 2026-09-19
  const [theme, setThemeState] = useState<Theme>(getStoredTheme);
  const [language, setLanguageState] = useState<string>(i18n.language);
  const [settingsOpen, setSettingsOpen] = useState(false);
  // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
  // const [isLiveOpen, setIsLiveOpen] = useState(false);

  const streamingRef = useRef<{ sessionId: string; messageId: string; taskId?: string } | null>(null);
  // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
  // const liveRespondRef = useRef<{ resolve: (text: string) => void; reject: (err: Error) => void } | null>(null);
  const toastTimer = useRef<number | null>(null);
  const mainRef = useRef<HTMLElement | null>(null);
  const isDraggingRef = useRef(false);

  function showToast(message: string) {
    setToast(message);
    if (toastTimer.current) window.clearTimeout(toastTimer.current);
    toastTimer.current = window.setTimeout(() => setToast(null), 2200);
  }

  // EMAIL_AUTH: добавлено 2026-09-19
  async function handleAuthSubmit(email: string, password: string) {
    if (authModal === 'register') {
      await register(email, password);
    } else {
      await login(email, password);
    }
    setAuthModal(null);
  }

  function handleLogout() {
    void logout();
  }

  // ADMIN_PANEL: добавлено 2026-09-19
  function handleOpenAdmin() {
    window.location.hash = '#/admin';
  }

  function handleUpgrade() {
    setUpgradeOpen(true);
  }

  function handleOpenSupport() {
    setSupportOpen(true);
  }

  function handleUpgradeBuy(planName: string) {
    showToast(t('upgrade.soon', { plan: planName }));
  }

  // SETTINGS: добавлено 2026-09-19 — theme/language changes apply immediately and persist.
  function handleThemeChange(next: Theme) {
    setThemeState(next);
    setTheme(next);
  }

  function handleLanguageChange(lang: string) {
    setLanguageState(lang);
    setLanguage(lang);
  }

  // ADMIN_PANEL: добавлено 2026-09-19 — hash-based routing for the admin page.
  useEffect(() => {
    const onHash = () => setRoute(window.location.hash);
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);

  const isAdminRoute = route.startsWith('#/admin');

  // Redirect non-admins away from the admin route.
  useEffect(() => {
    if (isAdminRoute && user && !user.isAdmin) {
      window.location.hash = '';
    }
  }, [isAdminRoute, user]);

  useEffect(() => {
    saveSessions(sessions);
  }, [sessions]);

  // SUBSCRIPTION_TIERS: добавлено 2026-09-17
  async function refreshUsage() {
    if (!token) return;
    try {
      setUsage(await getSubscriptionUsage());
    } catch {
      // Best-effort; the indicator simply stays unchanged on transient failures.
    }
  }

  useEffect(() => {
    void refreshUsage();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [token]);

  useEffect(() => {
    setAuthToken(token);

    if (!token) {
      void signalrService.disconnect();
      return;
    }

    let disposed = false;

    void signalrService.connect(token, {
      onContentToken: (delta) => {
        const ctx = streamingRef.current;
        if (!ctx || disposed) return;
        setSessions((prev) =>
          updateMessage(prev, ctx.sessionId, ctx.messageId, (m) => ({
            ...m,
            content: m.content + delta,
            // Hide the live action badge as soon as the final answer starts streaming.
            currentAction: null,
          })),
        );
      },
      onThinkingToken: (delta) => {
        const ctx = streamingRef.current;
        if (!ctx || disposed) return;
        setSessions((prev) =>
          updateMessage(prev, ctx.sessionId, ctx.messageId, (m) => ({ ...m, thinking: (m.thinking ?? '') + delta })),
        );
      },
      onLog: (message) => {
        const ctx = streamingRef.current;
        if (!ctx || disposed) return;
        setSessions((prev) =>
          updateMessage(prev, ctx.sessionId, ctx.messageId, (m) => ({ ...m, logs: [...(m.logs ?? []), message] })),
        );
      },
      onScreenshot: (base64) => {
        const ctx = streamingRef.current;
        if (!ctx || disposed) return;
        setSessions((prev) =>
          updateMessage(prev, ctx.sessionId, ctx.messageId, (m) => ({
            ...m,
            screenshots: [...(m.screenshots ?? []), base64],
          })),
        );
      },
      onAgentStatus: (payload) => {
        const ctx = streamingRef.current;
        if (!ctx || disposed) return;
        setAgentStatus(payload.label);
        setSessions((prev) =>
          updateMessage(prev, ctx.sessionId, ctx.messageId, (m) => ({ ...m, currentAction: payload })),
        );
      },
      onFileCreated: (payload) => {
        if (disposed) return;
        setFileCreatedEvent(payload);
      },
      onToolAction: (event) => {
        const ctx = streamingRef.current;
        if (!ctx || disposed) return;
        setSessions((prev) =>
          updateMessage(prev, ctx.sessionId, ctx.messageId, (m) => ({
            ...m,
            toolActions: [...(m.toolActions ?? []), event],
          })),
        );
        // Refresh the file tree when the editor creates/edits a file, so the
        // new/modified file appears in the IDE without a manual refresh.
        if (
          event.toolName === 'str_replace_editor' &&
          (event.command === 'create' || event.command === 'str_replace' || event.command === 'insert') &&
          event.status === 'completed'
        ) {
          setFileRefreshToken((t) => t + 1);
          setAgentFileChange({ path: event.path });
        }
      },
      onTodoUpdate: (payload) => {
        const ctx = streamingRef.current;
        if (!ctx || disposed) return;
        setSessions((prev) =>
          updateMessage(prev, ctx.sessionId, ctx.messageId, (m) => ({
            ...m,
            todos: payload.todos,
          })),
        );
      },
      onCompleted: (payload) => {
        // COMMAND_CONFIRM: добавлено 2026-09-20 — a finished task never auto-approves, and the
        // backend drops the flag in its own finally block.
        setPendingAction(null);
        setAllowAllTaskId(null);
        const ctx = streamingRef.current;
        if (!ctx || disposed) return;
        const { sessionId, messageId } = ctx;
        streamingRef.current = null;
        setAgentStatus('Completed');
        setSessions((prev) =>
          updateMessage(prev, sessionId, messageId, (m) => ({
            ...m,
            content: m.content || payload.result,
            status: 'complete',
          })),
        );
        setSessions((prev) => updateSession(prev, sessionId, (s) => ({ ...s, status: 'Completed' })));
        // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
        // const responder = liveRespondRef.current;
        // liveRespondRef.current = null;
        // responder?.resolve(payload.result);
        // SUBSCRIPTION_TIERS: добавлено 2026-09-17
        void refreshUsage();
      },
      onError: (err) => {
        // COMMAND_CONFIRM: добавлено 2026-09-20
        setPendingAction(null);
        setAllowAllTaskId(null);
        const ctx = streamingRef.current;
        if (!ctx || disposed) return;
        const { sessionId, messageId } = ctx;
        streamingRef.current = null;
        setAgentStatus('Failed');
        setSessions((prev) =>
          updateMessage(prev, sessionId, messageId, (m) => ({ ...m, status: 'error', error: err })),
        );
        setSessions((prev) => updateSession(prev, sessionId, (s) => ({ ...s, status: 'Failed' })));
        // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
        // const responder = liveRespondRef.current;
        // liveRespondRef.current = null;
        // responder?.reject(new Error(err));
        // SUBSCRIPTION_TIERS: добавлено 2026-09-17
        void refreshUsage();
      },
      onStopped: () => {
        // COMMAND_CONFIRM: добавлено 2026-09-20
        setPendingAction(null);
        setAllowAllTaskId(null);
        const ctx = streamingRef.current;
        if (!ctx || disposed) return;
        streamingRef.current = null;
        finalizeStopped(ctx);
        // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
        // const responder = liveRespondRef.current;
        // liveRespondRef.current = null;
        // responder?.reject(new Error('Генерация остановлена'));
      },
      onRunProjectError: (payload) => {
        if (disposed) return;
        showToast(t('toast.runError', { message: payload.message }));
      },
      onSearchStatus: (payload) => {
        if (disposed) return;
        const ctx = streamingRef.current;
        if (ctx) {
          const action = payload.status === 'searching'
            ? { stage: 'searching', label: payload.query ? t('agent.searching', { query: payload.query }) : t('agent.searchingShort') }
            : null;
          setSessions((prev) =>
            updateMessage(prev, ctx.sessionId, ctx.messageId, (m) => ({ ...m, currentAction: action })),
          );
        }
      },
      onProblems: (payload) => {
        if (disposed) return;
        const ctx = streamingRef.current;
        if (!ctx) return;
        setSessions((prev) =>
          updateMessage(prev, ctx.sessionId, ctx.messageId, (m) => ({ ...m, problems: payload.problems ?? [] })),
        );
      },
      // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
      onPendingActionCreated: (payload) => {
        if (disposed) return;
        setPendingAction(payload);
      },
    });

    return () => {
      disposed = true;
      void signalrService.disconnect();
    };
  }, [token]);

  const activeSession = sessions.find((s) => s.id === activeId) ?? null;
  const mode: 'chat' | 'code' = activeTab === 'projects' ? 'code' : 'chat';
  const students = activeTab === 'students';
  const isAgent = activeTab === 'projects';
  const agentRunning = activeSession?.status === 'Running';

  // Latest agent progress for the IDE bottom panel (Live Action Status / Todo).
  const lastAssistant = [...(activeSession?.messages ?? [])].reverse().find((m) => m.role === 'assistant') ?? null;
  const latestToolActions = lastAssistant?.toolActions ?? [];
  const latestTodos = lastAssistant?.todos ?? [];

  // Reset the status bar and editor cursor info when switching sessions.
  useEffect(() => {
    const s = sessions.find((x) => x.id === activeId);
    setAgentStatus(
      s?.status === 'Running'
        ? 'Working…'
        : s?.status === 'Completed'
          ? 'Completed'
          : s?.status === 'Failed'
            ? 'Failed'
            : 'Ready',
    );
    setCursorInfo(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeId]);

  function handleDividerMouseDown() {
    isDraggingRef.current = true;
  }

  useEffect(() => {
    function handleMouseMove(e: MouseEvent) {
      if (!isDraggingRef.current || !mainRef.current) return;
      const rect = mainRef.current.getBoundingClientRect();
      const newWidth = ((rect.right - e.clientX) / rect.width) * 100;
      if (newWidth >= 25 && newWidth <= 75) setWorkspaceWidth(newWidth);
    }
    function handleMouseUp() {
      isDraggingRef.current = false;
    }
    window.addEventListener('mousemove', handleMouseMove);
    window.addEventListener('mouseup', handleMouseUp);
    return () => {
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('mouseup', handleMouseUp);
    };
  }, []);

  function handleTabChange(next: ChatSessionKind) {
    setActiveTab(next);
    // A top-level tab switch opens a fresh, empty composer for that mode — it must
    // never auto-restore the last-opened chat of the previous (or current) mode.
    // We set a draft id (not null) so a workspace chatId is already available if the
    // user creates files manually before ever sending a message to the agent.
    setActiveId(uid());
    setThinking(false);
    setSmartSearch(false);
    setReasoningEffort('high');
    if (next === 'projects') setModel('conexy-coder');
    else if (next === 'students') setModel('ConexyV1-pro');
    else setModel('ConexyV1-flash');
  }

  function handleModelChange(next: ConexyModel) {
    setModel(next);
    // The reasoning/thinking toggle is only meaningful for ConexyV1-pro. Drop its state
    // when switching to a model that ignores it, so a stale value can't leak into a request.
    if (next !== 'ConexyV1-pro') {
      setThinking(false);
    }
  }

  function handleNewSession(kind: ChatSessionKind): string {
    // A brand-new chat exists only as the currently-open empty screen (a draft).
    // It is NOT added to the sidebar list and creates no backend entity until the
    // user sends the first message — see ensureSessionId, which materializes it.
    const draftId = uid();
    setActiveId(draftId);
    return draftId;
  }

  function ensureSessionId(kind: ChatSessionKind): string {
    const current = sessions.find((s) => s.id === activeId);
    if (current && (current.kind ?? 'chat') === kind) return current.id;

    // First message: turn the draft (or a fresh id) into a real session.
    const id = !current ? (activeId ?? uid()) : uid();
    const session: ChatSession = {
      id,
      title: defaultTitle(kind, t),
      status: 'Idle',
      model,
      kind,
      messages: [],
      createdAt: Date.now(),
    };
    setSessions((prev) => [session, ...prev]);
    setActiveId(id);
    return id;
  }

  // Materializes the current agent draft (or reuses the active session) so manual file
  // operations have a stable chatId/workspace — the user no longer has to send a message
  // to the agent before they can start creating files.
  function ensureAgentWorkspace(): string {
    const existing = sessions.find((s) => s.id === activeId);
    if (existing) return existing.id;

    const id = activeId ?? uid();
    const session: ChatSession = {
      id,
      title: defaultTitle('projects', t),
      status: 'Idle',
      model: 'conexy-coder',
      kind: 'projects',
      messages: [],
      createdAt: Date.now(),
    };
    setSessions((prev) => [session, ...prev]);
    setActiveId(id);
    return id;
  }

  function handlePinSession(id: string) {
    setSessions((prev) => prev.map((s) => (s.id === id ? { ...s, isPinned: !s.isPinned } : s)));
  }

  function handleRenameSession(id: string, title: string) {
    setSessions((prev) => prev.map((s) => (s.id === id ? { ...s, title } : s)));
  }

  function handleDeleteSession(id: string) {
    // Clear every reference to the deleted chat so it can never be re-opened:
    // the session list, the active id, and any in-flight stream still targeting
    // it (whose late SignalR deltas could otherwise re-attach messages).
    if (streamingRef.current?.sessionId === id) {
      streamingRef.current = null;
    }
    setSessions((prev) => prev.filter((s) => s.id !== id));
    if (activeId === id) {
      setActiveId(uid());
    }
  }

  async function handleShareSession(session: ChatSession) {
    const text = session.messages
      .map((m) => `${m.role === 'user' ? 'User' : 'ConexyAI'}: ${m.content || '(attachment)'}`)
      .join('\n\n');
    try {
      await navigator.clipboard.writeText(text);
      showToast('Copied to clipboard');
    } catch {
      showToast('Failed to copy');
    }
  }

  async function startCompletion(
    sessionId: string,
    prompt: string,
    attachments: TaskAttachment[],
    appendUserMessage: boolean,
  ) {
    setAgentStatus('Working…');
    const kind = sessions.find((s) => s.id === sessionId)?.kind ?? activeTab;
    const currentTaskId = sessions.find((s) => s.id === sessionId)?.taskId;

    const userMsg: ChatMessage = {
      id: uid(),
      role: 'user',
      content: prompt,
      thinking: '',
      status: 'complete',
      logs: [],
      screenshots: [],
      createdAt: Date.now(),
      model,
    };
    const assistantMsg: ChatMessage = {
      id: uid(),
      role: 'assistant',
      content: '',
      thinking: '',
      status: 'streaming',
      logs: [],
      screenshots: [],
      createdAt: Date.now(),
      model,
    };

    setSessions((prev) =>
      updateSession(prev, sessionId, (s) => ({
        ...s,
        title: s.title === defaultTitle(kind, t) ? prompt.slice(0, 40) : s.title,
        status: 'Running',
        model,
        messages: appendUserMessage ? [...s.messages, userMsg, assistantMsg] : [...s.messages, assistantMsg],
      })),
    );

    try {
      const res = await runTask({
        model,
        prompt,
        attachments: attachments.length ? attachments : undefined,
        thinking,
        reasoningEffort,
        smartSearch,
        studentsMode: students,
        sessionId: currentTaskId,
        chatId: sessionId,
      });

      setSessions((prev) => updateSession(prev, sessionId, (s) => ({ ...s, taskId: res.id })));
      streamingRef.current = { sessionId, messageId: assistantMsg.id, taskId: res.id };
      await signalrService.joinTask(res.id);
    } catch (e) {
      // SUBSCRIPTION_TIERS: добавлено 2026-09-17
      const data = (e as { response?: { data?: { error?: string; limit?: string; resetsAt?: string } } })?.response?.data;
      if (data?.error === 'LIMIT_EXCEEDED') {
        setLimitExceeded({ limit: data.limit ?? 'unknown', resetsAt: data.resetsAt ?? '' });
        setUpgradeOpen(true);
        setSessions((prev) =>
          updateSession(prev, sessionId, (s) => ({
            ...s,
            status: 'Failed',
            messages: s.messages.map((m) =>
              m.id === assistantMsg.id ? { ...m, status: 'error', error: t('agent.limitExceeded') } : m,
            ),
          })),
        );
        void refreshUsage();
        return;
      }

      const message = e instanceof Error ? e.message : String(e);
      streamingRef.current = null;
      setSessions((prev) =>
        updateSession(prev, sessionId, (s) => ({
          ...s,
          status: 'Failed',
          messages: s.messages.map((m) =>
            m.id === assistantMsg.id ? { ...m, status: 'error', error: message } : m,
          ),
        })),
      );
      // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
      // const responder = liveRespondRef.current;
      // liveRespondRef.current = null;
      // responder?.reject(new Error(message));
    }
  }

  async function handleSend(prompt: string, attachments: TaskAttachment[]) {
    if (!prompt.trim() || !token) return;
    const sessionId = ensureSessionId(activeTab);
    await startCompletion(sessionId, prompt, attachments, true);
  }

  // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
  /*
  function sendLiveMessage(text: string): Promise<string> {
    return new Promise<string>((resolve, reject) => {
      if (!token || streamingRef.current) {
        reject(new Error('Генерация уже выполняется'));
        return;
      }
      const sessionId = ensureSessionId(activeTab);

      // Safety net: never let the voice loop hang on "Думаю..." forever.
      const timeoutId = window.setTimeout(() => {
        liveRespondRef.current = null;
        reject(new Error('Время ожидания ответа модели истекло'));
      }, 60_000);

      liveRespondRef.current = {
        resolve: (result) => {
          window.clearTimeout(timeoutId);
          resolve(result);
        },
        reject: (err) => {
          window.clearTimeout(timeoutId);
          reject(err);
        },
      };
      void startCompletion(sessionId, text, [], true);
    });
  }
  */

  function handleRegenerate(assistantMessageId: string) {
    if (!token || streamingRef.current) return;
    const session = sessions.find((s) => s.id === activeId);
    if (!session) return;
    const idx = session.messages.findIndex((m) => m.id === assistantMessageId);
    if (idx < 0) return;

    // Find the user prompt this reply was answering.
    let prompt = '';
    for (let i = idx - 1; i >= 0; i--) {
      const m = session.messages[i];
      if (m.role === 'user') {
        prompt = m.content;
        break;
      }
    }
    if (!prompt.trim()) return;

    void startCompletion(session.id, prompt, [], false);
  }

  function handleResend(messageId: string) {
    if (!token || streamingRef.current) return;
    const session = sessions.find((s) => s.id === activeId);
    if (!session) return;
    const message = session.messages.find((m) => m.id === messageId);
    if (!message || message.role !== 'user') return;
    void startCompletion(session.id, message.content, [], true);
  }

  function handleEditMessage(messageId: string, newContent: string) {
    if (!token || streamingRef.current) return;
    const session = sessions.find((s) => s.id === activeId);
    if (!session) return;
    const message = session.messages.find((m) => m.id === messageId);
    if (!message || message.role !== 'user') return;
    void startCompletion(session.id, newContent, [], true);
  }

  function finalizeStopped(ctx: { sessionId: string; messageId: string }) {
    setAgentStatus('Stopped');
    setSessions((prev) =>
      updateMessage(prev, ctx.sessionId, ctx.messageId, (m) => {
        if (m.status === 'stopped') return m;
        const note = t('agent.generationStopped');
        return {
          ...m,
          status: 'stopped',
          content: m.content ? `${m.content}\n\n${note}` : note,
        };
      }),
    );
    setSessions((prev) => updateSession(prev, ctx.sessionId, (s) => ({ ...s, status: 'Stopped' })));
  }

  function handleStop() {
    const ctx = streamingRef.current;
    if (!ctx) return;
    const taskId = ctx.taskId ?? activeSession?.taskId;
    if (taskId) void signalrService.stopGeneration(taskId);

    // Flip the UI immediately; the backend OnStopped event is idempotent and will be a no-op.
    streamingRef.current = null;
    finalizeStopped(ctx);
  }

  // COMMAND_CONFIRM: добавлено 2026-09-20
  async function handleCommandDecision(actionId: string, approved: boolean, allowAll: boolean) {
    const taskId = pendingAction?.taskId ?? streamingRef.current?.taskId ?? activeSession?.taskId;
    setPendingAction(null);
    if (approved && allowAll && taskId) setAllowAllTaskId(taskId);
    try {
      await signalrService.confirmAction(actionId, approved, allowAll);
    } catch (err) {
      console.error('[CommandConfirm] ConfirmAction failed:', err);
      showToast(t('toast.confirmFailed'));
    }
  }

  // COMMAND_CONFIRM: added 2026-09-20 — the user asked to be asked again for this task.
  async function handleDisableAllowAll() {
    const taskId = allowAllTaskId;
    setAllowAllTaskId(null);
    if (!taskId) return;
    try {
      await signalrService.setTaskAutoApproval(taskId, false);
    } catch (err) {
      console.error('[CommandConfirm] SetTaskAutoApproval failed:', err);
    }
  }

  // ADMIN_PANEL: добавлено 2026-09-19 — render the admin page as a full-screen view.
  if (isAdminRoute && user?.isAdmin) {
    return <AdminPanel onBack={() => { window.location.hash = ''; }} onToast={showToast} />;
  }

  // COMMAND_CONFIRM: добавлено 2026-09-20
  const commandApproval: CommandApproval = {
    onDecision: (actionId, approved, allowAll) => {
      void handleCommandDecision(actionId, approved, allowAll);
    },
    allowAllEnabled: allowAllTaskId != null,
    onDisableAllowAll: () => {
      void handleDisableAllowAll();
    },
  };

  return (
    <div className="app">
      <Sidebar
        open={sidebarOpen}
        activeTab={activeTab}
        search={search}
        sessions={sessions}
        activeId={activeId}
        onToggle={() => setSidebarOpen((o) => !o)}
        onTabChange={(tab) => {
          handleTabChange(tab);
          if (isMobile) setSidebarOpen(false);
        }}
        onNewSession={(kind) => {
          handleNewSession(kind);
          if (isMobile) setSidebarOpen(false);
        }}
        onSelectSession={(id) => {
          setActiveId(id);
          if (isMobile) setSidebarOpen(false);
        }}
        onSearchChange={setSearch}
        onShareSession={handleShareSession}
        onPinSession={handlePinSession}
        onRenameSession={handleRenameSession}
        onDeleteSession={handleDeleteSession}
        user={user}
        onLogin={() => setAuthModal('login')}
        onRegister={() => setAuthModal('register')}
        onLogout={handleLogout}
        onUpgrade={handleUpgrade}
        onOpenAdmin={handleOpenAdmin}
        onOpenSupport={handleOpenSupport}
        onOpenSettings={() => setSettingsOpen(true)}
      />

      {/* MOBILE: добавлено 2026-09-19 — floating hamburger + backdrop for the overlay sidebar. */}
      {isMobile && (
        <button
          className="mobile-menu-btn"
          onClick={() => setSidebarOpen(true)}
          aria-label={t('sidebar.menu')}
          type="button"
        >
          <MenuIcon size={20} />
        </button>
      )}
      {isMobile && sidebarOpen && (
        <div className="sidebar-backdrop" onClick={() => setSidebarOpen(false)} />
      )}

      <main className="main" ref={mainRef}>
        <div className={isAgent ? 'main__body main__body--ide' : 'main__body'}>
          <div
            className={isAgent && !ideCollapsed && !isMobile ? 'ide-chat' : 'ide-chat--single'}
            style={isAgent && !ideCollapsed && !isMobile ? { flex: `0 0 ${100 - workspaceWidth}%` } : undefined}
          >
            {/* Mobile: the model picker lives in the chat header. */}
            {isMobile && (
              <div className="chat-header">
                <ModelPicker
                  model={model}
                  onModelChange={handleModelChange}
                  mode={mode}
                  thinking={thinking}
                  onThinkingChange={setThinking}
                  reasoningEffort={reasoningEffort}
                  onReasoningEffortChange={setReasoningEffort}
                  smartSearch={smartSearch}
                  onSmartSearchChange={setSmartSearch}
                  locked={students}
                />
                {isAgent && (
                  <div className="chat-header__actions">
                    {/* SUBSCRIPTION_TIERS: добавлено 2026-09-17 */}
                    <UsageIndicator usage={usage} />
                  </div>
                )}
              </div>
            )}
            {/* Desktop agent toolbar (the model picker stays in the composer). */}
            {!isMobile && isAgent && (
              <div className="ide-toolbar">
                <button
                  className="icon-btn"
                  onClick={() => setIdeCollapsed((c) => !c)}
                  title={ideCollapsed ? t('workspace.showIde') : t('workspace.hideIde')}
                  aria-label={ideCollapsed ? t('workspace.showIde') : t('workspace.hideIde')}
                >
                  {ideCollapsed ? <PanelRightOpenIcon size={18} /> : <PanelRightCloseIcon size={18} />}
                </button>
                {/* SUBSCRIPTION_TIERS: добавлено 2026-09-17 */}
                <UsageIndicator usage={usage} />
              </div>
            )}
            {token ? (
              <ChatFeed
                session={activeSession}
                nickname={user?.displayName ? user.displayName.split('@')[0] : null}
                onRegenerate={handleRegenerate}
                onResend={handleResend}
                onEditMessage={handleEditMessage}
                commandApproval={commandApproval}
              />
            ) : initializing ? (
              <div className="feed feed--empty">
                <p className="muted">{t('common.loading')}</p>
              </div>
            ) : (
              <div className="feed feed--empty">
                <div className="hero">
                  <h1 className="hero__title">{t('hero.title')}</h1>
                  <p className="hero__subtitle">{t('hero.subtitle')}</p>
                </div>
              </div>
            )}
            <InputBar
              model={model}
              onModelChange={handleModelChange}
              mode={mode}
              thinking={thinking}
              onThinkingChange={setThinking}
              reasoningEffort={reasoningEffort}
              onReasoningEffortChange={setReasoningEffort}
              smartSearch={smartSearch}
              onSmartSearchChange={setSmartSearch}
              locked={students}
              disabled={!token}
              isGenerating={agentRunning}
              onStop={handleStop}
              // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
              // onOpenLive={() => setIsLiveOpen(true)}
              onSend={handleSend}
            />
          </div>

          {isAgent && !isMobile && !ideCollapsed && (
            <>
              <div className="ide-divider" onMouseDown={handleDividerMouseDown} />
              <WorkspacePanel
                sessionId={activeSession?.id ?? activeId ?? undefined}
                onEnsureWorkspace={ensureAgentWorkspace}
                running={agentRunning}
                fileCreatedEvent={fileCreatedEvent}
                fileRefreshToken={fileRefreshToken}
                agentFileChange={agentFileChange}
                toolActions={latestToolActions}
                todos={latestTodos}
                commandApproval={commandApproval}
                onCursorChange={setCursorInfo}
                onRunInSeparateWindow={() => showToast(t('toast.runProject'))}
                style={{ flex: `0 0 ${workspaceWidth}%` }}
              />
            </>
          )}
        </div>
        <StatusBar agentStatus={agentStatus} cursor={cursorInfo} />
      </main>

      {toast && <div className="toast">{toast}</div>}
      {authError && <div className="toast toast--error">{authError}</div>}

      {/* LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
      {isLiveOpen && (
        <LiveVoiceModal
          onClose={() => setIsLiveOpen(false)}
          onSendMessage={sendLiveMessage}
        />
      )}
      */}

      {/* COMMAND_CONFIRM: добавлено 2026-09-20
          Confirmation is rendered inline in the action feed (CommandConfirmCard), so nothing
          covers the chat while the agent waits for the user's decision. */}

      {/* SUBSCRIPTION_TIERS: добавлено 2026-09-17 */}
      {upgradeOpen && (
        <UpgradeModal
          limitInfo={limitExceeded}
          onClose={() => {
            setUpgradeOpen(false);
            setLimitExceeded(null);
          }}
          onBuy={handleUpgradeBuy}
        />
      )}

      {/* EMAIL_AUTH: добавлено 2026-09-19 */}
      {authModal && (
        <AuthModal
          mode={authModal}
          onSubmit={handleAuthSubmit}
          onSwitchMode={() => setAuthModal(authModal === 'login' ? 'register' : 'login')}
          onClose={() => setAuthModal(null)}
        />
      )}

      {/* SUPPORT: добавлено 2026-09-19 */}
      {supportOpen && <SupportChat onClose={() => setSupportOpen(false)} onToast={showToast} />}

      {/* SETTINGS: добавлено 2026-09-19 */}
      {settingsOpen && (
        <SettingsModal
          theme={theme}
          language={language}
          onThemeChange={handleThemeChange}
          onLanguageChange={handleLanguageChange}
          onClose={() => setSettingsOpen(false)}
        />
      )}
    </div>
  );
}
