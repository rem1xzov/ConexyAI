import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { setAuthToken } from './api/client';
import { getChats, getChatTranscript, deleteChat, getSubscriptionUsage, getTaskStatus, runTask } from './api/conexyApi';
import { signalrService } from './services/signalrService';
import { useAuth } from './hooks/useAuth';
import { useIsMobile } from './hooks/useMediaQuery';
import { Sidebar } from './components/Sidebar';
import { ChatFeed } from './components/ChatFeed';
import { InputBar } from './components/InputBar';
import { ModelPicker } from './components/ModelPicker';
import { WorkspacePanel } from './components/WorkspacePanel';
import { StatusBar } from './components/StatusBar';
import { ConnectionBanner } from './components/ConnectionBanner';
// LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
// import { LiveVoiceModal } from './components/LiveVoiceModal';
import { MenuIcon, PanelRightCloseIcon, PanelRightOpenIcon, GhostIcon } from './components/Icons';
// LOGO_SWEEP / CLAUDE_LAYOUT: добавлено 2026-09-20
import { ConexyLogo } from './components/ConexyLogo';
import { ChatLayout, type ChatQuickChip } from './components/ChatLayout';
// GUEST_HERO: добавлено 2026-09-20
import { GuestHero } from './components/GuestHero';
// SUBSCRIPTION_TIERS: добавлено 2026-09-17
import { UsageIndicator } from './components/UsageIndicator';
import { UpgradeModal } from './components/UpgradeModal';
// ADMIN_PANEL: добавлено 2026-09-19
import { AdminPanel } from './components/AdminPanel';
// ADMIN_ERROR_BOUNDARY: добавлено 2026-09-23
import { ErrorBoundary } from './components/ErrorBoundary';
// SUPPORT: добавлено 2026-09-19
import { SupportChat } from './components/SupportChat';
// EMAIL_AUTH: добавлено 2026-09-19
import { AuthModal } from './components/AuthModal';
import type { AuthMode } from './components/AuthModal';
import { SettingsModal } from './components/SettingsModal';
import { getStoredTheme, setTheme, type Theme } from './theme';
import { setLanguage } from './i18n';
import type { ConexyModel, LimitExceededInfo, ReasoningEffort, SendOutcome, SubscriptionUsage, TaskAttachment, ChatSummary, ChatTranscript } from './types/api';
import type { ChatMessage, ChatSession, ChatSessionKind, AgentStep } from './types/chat';
import type { PendingActionPayload } from './types/signalr';
// ATTACHMENTS_IN_BUBBLE: добавлено 2026-09-21
import { toMessageAttachment } from './utils/attachments';
// DEPLOY_WINDOW_GRACEFUL_ERRORS: добавлено 2026-09-23
import { humanError } from './utils/humanError';

const STORAGE_KEY = 'conexy_sessions';

// CHAT_DELETE: маршрут удаления объявлен как {chatId:guid}, поэтому запрос на не-UUID id чата
// смысла не имеет (старые локальные сессии).
const GUID_LIKE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function uid(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }

  // CHAT_SYNC: fallback обязателен именно в форме UUID. Идентификатор чата уезжает на сервер как
  // `chatId`, и история диалога пишется под ним же; если он не парсится как Guid, бэкенд молча
  // подменяет chatId на taskId — и такой чат потом невозможно ни синхронизировать, ни найти.
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === 'x' ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

// AGENT_TIMELINE: добавлено 2026-09-22
/**
 * Closes the still-running reasoning step. Called when the run finishes, fails or is stopped, so
 * the step's timer freezes at its real duration instead of ticking forever.
 */
function closeSteps(steps?: AgentStep[]): AgentStep[] | undefined {
  if (!steps || steps.length === 0) return steps;
  const last = steps[steps.length - 1];
  if (last.endedAt !== undefined) return steps;
  const now = Date.now();
  return steps.map((s) => (s.id === last.id ? { ...s, endedAt: now } : s));
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

// COWORK_MODE: добавлено 2026-09-23 — оба режима агента живут во вкладке «Агент».
function isAgentModel(model: ConexyModel): boolean {
  return model === 'conexy-coder' || model === 'conexy-cowork';
}

function normalizeSession(s: ChatSession): ChatSession {
  const raw = s.model as string;
  const model: ConexyModel =
    raw === 'conexy-coder' || raw === 'Conexy-coder' ? 'conexy-coder'
    : raw === 'conexy-cowork' ? 'conexy-cowork'
    : raw === 'ConexyV1-pro' ? 'ConexyV1-pro'
    : 'ConexyV1-flash';
  return {
    ...s,
    model,
    kind: s.kind ?? (isAgentModel(model) ? 'projects' : 'chat'),
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

// CHAT_KIND_SYNC: добавлено 2026-09-23
/**
 * The tab a server chat belongs to. The mode is persisted with the history, so a chat synced to a
 * second device reopens in the same tab — including the students tab, which cannot be inferred from
 * anything else. Unknown or missing values fall back to the generic chat tab.
 */
function kindFromServer(kind: string | null | undefined): ChatSessionKind {
  return kind === 'projects' || kind === 'students' ? kind : 'chat';
}

// CHAT_SYNC: добавлено 2026-09-23
/**
 * Builds a local session from a server chat list entry plus its stored transcript. Used for chats
 * this device has never seen (created on another device), which is what makes the sidebar no longer
 * device-local.
 */
function sessionFromServer(
  chat: ChatSummary,
  transcript: ChatTranscript | null,
  fallbackTitle: string,
): ChatSession {
  const kind = kindFromServer(chat.kind);

  const messages: ChatMessage[] = (transcript?.messages ?? [])
    // Only real turns belong in the transcript; tool/system rows are internal plumbing.
    .filter((m) => m.role === 'user' || m.role === 'assistant')
    .map((m) => ({
      id: uid(),
      role: m.role as 'user' | 'assistant',
      content: m.content,
      status: 'complete' as const,
      createdAt: new Date(m.createdAt).getTime(),
    }));

  return {
    id: chat.id,
    title: chat.title || fallbackTitle,
    status: 'Completed',
    // CHAT_DELETE: чат пришёл с сервера, поэтому его можно и удалять локально, если сервер
    // перестанет его отдавать (см. prune в syncChats).
    remote: true,
    // The agent tab has its own models (the picker remembers which one); students always runs Pro;
    // in the plain chat tab the model is chosen per message anyway.
    model: kind === 'projects' ? 'conexy-coder' : kind === 'students' ? 'ConexyV1-pro' : 'ConexyV1-flash',
    kind,
    messages,
    createdAt: messages.length > 0 ? messages[0].createdAt : new Date(chat.lastActivityAt).getTime(),
  };
}

// LOGO_SWEEP: добавлено 2026-09-20 — time-of-day greeting for the start screen.
// TABS_UNIFIED: each tab has its own full sentence, so translators get a complete string
// instead of a concatenation.
function greetingKey(kind: ChatSessionKind): string {
  const hour = new Date().getHours();
  const time = hour >= 5 && hour < 12
    ? 'Morning'
    : hour >= 12 && hour < 18
      ? 'Afternoon'
      : hour >= 18 && hour < 23
        ? 'Evening'
        : 'Night';

  const scope = kind === 'projects' ? 'agent' : kind === 'students' ? 'students' : 'chat';
  return `${scope}.good${time}`;
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
  // INCOGNITO_CHAT: добавлено 2026-09-20
  // Local-only switch for the plain chat tab. It is never persisted and is reset whenever the
  // user moves to another chat, so an incognito session can never be resumed or reused.
  const [incognito, setIncognito] = useState(false);
  const [search, setSearch] = useState('');
  const [sidebarOpen, setSidebarOpen] = useState(() => !isMobile);
  const [workspaceWidth, setWorkspaceWidth] = useState(55); // % width of the IDE pane
  const [ideCollapsed, setIdeCollapsed] = useState(false);

  const [sessions, setSessions] = useState<ChatSession[]>(loadSessions);
  // CHAT_SYNC: добавлено 2026-09-23 — снимок списка чатов для синхронизации с сервером.
  const sessionsRef = useRef(sessions);
  sessionsRef.current = sessions;
  // Every visit starts on a fresh, empty chat with the default model instead of restoring
  // the last opened session. Previous chats stay available from the sidebar.
  const [activeId, setActiveId] = useState<string | null>(() => uid());
  // CHAT_DELETE: тот же приём для активного чата — синхронизация должна знать, какой чат сейчас
  // открыт, чтобы не выдернуть его из-под пользователя при прунинге.
  const activeIdRef = useRef(activeId);
  activeIdRef.current = activeId;
  const [toast, setToast] = useState<string | null>(null);
  const [fileCreatedEvent, setFileCreatedEvent] = useState<{ path: string; name: string } | null>(null);
  // AGENT_FEED_ZED: добавлено 2026-09-23 — запрос «открой этот файл» из ленты шагов агента.
  // Воркспейс владеет своими вкладками, поэтому лента не может открыть файл напрямую: она шлёт
  // запрос, а `WorkspacePanel` его отрабатывает. nonce нужен, чтобы повторный клик по тому же
  // пути снова запустил эффект (одинаковый объект React бы проигнорировал).
  const [openFileRequest, setOpenFileRequest] = useState<{ path: string; nonce: number } | null>(null);
  const openFileNonceRef = useRef(0);
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
  // TASK_COMPLETION_DIAGNOSTICS: добавлено 2026-09-23 — номер тика сторожа для логов.
  const watchdogTickRef = useRef(0);

  // SIGNALR_RESILIENCE: добавлено 2026-09-22
  /**
   * Сверяет ход выполнения с сервером после обрыва связи (или перезагрузки страницы). События,
   * прошедшие пока сокет лежал, потеряны навсегда, поэтому единственный источник правды — сама
   * запись задачи. Без этого UI продолжал крутить спиннер по уже завершившейся задаче.
   */
  const resyncTurn = useCallback(
    async (sessionId: string, messageId: string, taskId: string, tick = 0): Promise<void> => {
      try {
        const res = await getTaskStatus(taskId);
        const status = (res.status ?? '').toLowerCase();
        // Снимок до того, как мы могли обнулить streamingRef: нужен для лога, иначе он всегда false.
        const wasStreaming = streamingRef.current?.messageId === messageId;

        // TASK_COMPLETION_WATCHDOG: добавлено 2026-09-22
        // TASK_COMPLETION_DIAGNOSTICS: расширено 2026-09-23 — при следующем воспроизведении
        // «висит генерация» по этой строке видно ЦЕЛИКОМ решение сторожа: на каком тике он сработал,
        // какой статус реально вернул сервер и что из этого следует. Раньше удачный опрос «running»
        // вообще ничего не писал, и нельзя было отличить «сторож не вызвался» от «вызвался, но статус
        // не тот».
        console.info('[Watchdog] poll', { taskId, tick, status, resultChars: res.result?.length ?? 0 });

        if (status === 'running' || status === 'pending') {
          // Задача жива: гарантируем членство в её группах, чтобы поток событий возобновился.
          void signalrService.ensureGroup(taskId).catch((e: unknown) => {
            console.warn('[Watchdog] ensureGroup failed', { taskId, error: String(e) });
          });
          return;
        }

        // TASK_COMPLETION_WATCHDOG: добавлено 2026-09-22 — терминальный статус на сервере
        // означает, что мы больше не в стриме. Без этого сторож продолжал бы опрос, а кнопка
        // «Стоп» оставалась бы доступной по уже законченной задаче.
        if (streamingRef.current?.messageId === messageId) {
          streamingRef.current = null;
        }

        setSessions((prev) =>
          updateMessage(prev, sessionId, messageId, (m) => {
            if (status === 'completed') {
              return {
                ...m,
                content: m.content || (res.result ?? ''),
                status: 'complete',
                steps: closeSteps(m.steps),
              };
            }
            if (status === 'cancelled') {
              return { ...m, status: 'stopped', steps: closeSteps(m.steps) };
            }
            return {
              ...m,
              status: 'error',
              error: res.result || t('agent.taskFailed'),
              steps: closeSteps(m.steps),
            };
          }),
        );
        setSessions((prev) =>
          updateSession(prev, sessionId, (s) => ({
            ...s,
            status:
              status === 'completed' ? 'Completed' : status === 'cancelled' ? 'Stopped' : 'Failed',
          })),
        );

        // Keep the status bar honest: the run is over even if no live event told us so.
        setAgentStatus(
          status === 'completed' ? 'Completed' : status === 'cancelled' ? 'Stopped' : 'Failed',
        );

        console.info('[Watchdog] finalizing turn from server state', {
          sessionId,
          taskId,
          tick,
          status,
          wasStreaming,
        });
      } catch (err) {
        // Недоступная/чужая задача не фатальна — транскрипт остаётся как есть. Но молчать нельзя:
        // именно проглатывание ошибки опроса делало баг невидимым (502 от прокси = вечный спиннер).
        console.warn('[Watchdog] could not read task state', { taskId, tick, error: String(err) });
      }
    },
    [t],
  );

  // TASK_COMPLETION_WATCHDOG: добавлено 2026-09-22
  // Гарантия, что генерация завершается САМА. Живое событие OnCompleted приходит по SignalR и в
  // редких случаях может быть потеряно (короткий прогон завершился до подписки на группу,
  // переподключение, обрыв). Раньше в этой ситуации интерфейс оставался в состоянии «генерирует»
  // навсегда, и пользователь был вынужден жать «Стоп». Теперь состояние сверяется с записью
  // задачи, пока идёт стрим, и сообщение закрывается автоматически.
  useEffect(() => {
    const id = window.setInterval(() => {
      watchdogTickRef.current += 1;
      const ctx = streamingRef.current;
      if (!ctx) return;
      if (!ctx.taskId) {
        // Раньше это молча выключало сторожа: ход «генерируется», а опрашивать нечего. Такой
        // случай обязан быть виден в консоли, иначе его невозможно отличить от «сторож не работает».
        console.warn('[Watchdog] streaming turn has no taskId — completion cannot be verified', {
          tick: watchdogTickRef.current,
          sessionId: ctx.sessionId,
          messageId: ctx.messageId,
        });
        return;
      }
      void resyncTurn(ctx.sessionId, ctx.messageId, ctx.taskId, watchdogTickRef.current);
    }, 5000);
    return () => window.clearInterval(id);
  }, [resyncTurn]);

  // SIGNALR_RESILIENCE: добавлено 2026-09-22 — после перезагрузки в localStorage может остаться ход,
  // который был в процессе, когда вкладка закрылась: статус «streaming» без единого события о
  // завершении. Сверяем такие ходы с сервером один раз за загрузку страницы.
  const didInitialResyncRef = useRef(false);
  useEffect(() => {
    if (!token || didInitialResyncRef.current) return;
    didInitialResyncRef.current = true;

    for (const session of sessions) {
      if (!session.taskId) continue;
      const last = session.messages[session.messages.length - 1];
      if (!last || last.role !== 'assistant' || last.status !== 'streaming') continue;
      // Ход, который ведёт текущая вкладка, уже отслеживается живьём — не мешаем ему.
      if (streamingRef.current?.messageId === last.id) continue;
      void resyncTurn(session.id, last.id, session.taskId);
    }
  }, [token, sessions, resyncTurn]);
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

  // ADMIN_PANEL_FIX: добавлено 2026-09-23 — маршрут админки решается ЯВНО.
  // Раньше при `user === null` (профиль ещё не приехал или /auth/me упал) не выполнялось ни одно из
  // условий: админка не рендерилась, авторедирект не срабатывал (он требует непустого user), и
  // клик по пункту меню выглядел как «ничего не произошло». Плюс авторедирект молча менял хеш, так
  // что пользователь не понимал, почему экран не открылся. Теперь на `#/admin` всегда есть
  // определённый исход: загрузка (пока профиль неизвестен), явное «нет доступа» или сама панель.
  const adminView = (() => {
    if (!isAdminRoute) return null;

    if (!user) {
      return (
        <div className="route-fallback">
          <span className="route-fallback__spinner" aria-hidden="true" />
          <p className="route-fallback__text">{t('common.loading')}</p>
          <button className="admin-btn" type="button" onClick={() => { window.location.hash = ''; }}>
            {t('admin.backToChat')}
          </button>
        </div>
      );
    }

    if (!user.isAdmin) {
      return (
        <div className="route-fallback">
          <p className="route-fallback__text">{t('admin.noAccess')}</p>
          <button className="admin-btn" type="button" onClick={() => { window.location.hash = ''; }}>
            {t('admin.backToChat')}
          </button>
        </div>
      );
    }

    return null; // admin and authenticated -> the panel is rendered below
  })();

  useEffect(() => {
    // INCOGNITO_CHAT: добавлено 2026-09-20 — incognito chats stay in memory for the current
    // visit only, so they are filtered out of the persisted list (and the sidebar).
    saveSessions(sessions.filter((s) => !s.incognito));
  }, [sessions]);

  // CHAT_SYNC: добавлено 2026-09-23
  // Список чатов раньше жил только в localStorage этого браузера — отсюда «разные чаты» на ПК и
  // телефоне под одним аккаунтом. Теперь список сверяется с сервером, и чаты, которых локально нет
  // (созданные на другом устройстве), подтягиваются вместе с историей.
  // Существующие локальные чаты НЕ перезаписываются: в них больше состояния (шаги агента, вложения,
  // todo), и они свежее, чем то, что успело доехать до истории.
  // CHAT_SYNC_FOCUS: добавлено 2026-09-23 — сверка вызывается не только при появлении токена, но и
  // при возвращении пользователя во вкладку (focus/visibilitychange), с debounce ниже.
  const chatSyncRef = useRef({ inFlight: false, lastStartedAt: 0, lastToken: null as string | null });

  const syncChats = useCallback(async () => {
    if (!token) return;

    const state = chatSyncRef.current;
    // Один запрос за раз: параллельные тики (focus + visibilitychange) не должны дублироваться.
    if (state.inFlight) return;
    state.inFlight = true;
    state.lastStartedAt = Date.now();

    try {
      const chats = await getChats();
      const serverIds = new Set(chats.map((c) => c.id));
      const known = new Set(sessionsRef.current.map((s) => s.id));
      const missing = chats.filter((c) => !known.has(c.id));

      // Ничего нового нет, но могли удалить что-то на другом устройстве — прунинг всё равно нужен.
      const restored = missing.length === 0
        ? []
        : await Promise.all(
            missing.map(async (chat) => {
              // A missing transcript must not drop the chat itself from the list.
              const transcript = await getChatTranscript(chat.id).catch((e: unknown) => {
                console.warn('[ChatSync] transcript unavailable', { chatId: chat.id, error: String(e) });
                return null;
              });
              return sessionFromServer(chat, transcript, defaultTitle(kindFromServer(chat.kind), t));
            }),
          );

      let added = 0;
      let pruned = 0;
      setSessions((prev) => {
        const existing = new Set(prev.map((s) => s.id));
        const fresh = restored.filter((s) => !existing.has(s.id));
        // CHAT_DELETE: для чатов, которые были на сервере, источник правды — сервер. Локальная
        // сессия, помеченная `remote`, которой больше нет в списке, была удалена (возможно с
        // другого устройства) — убираем и здесь, иначе удалённые чаты «воскресают» в сайдбаре.
        // Не трогаем: локальные черновики (нет серверной записи), активный чат (не выдёргиваем
        // открытый экран) и чат с идущим прогоном (его история ещё не записана).
        const kept = prev.filter((s) => {
          if (s.incognito || !s.remote) return true;
          if (s.id === activeIdRef.current) return true;
          if (streamingRef.current?.sessionId === s.id) return true;
          return serverIds.has(s.id);
        });

        added = fresh.length;
        pruned = prev.length - kept.length;
        if (fresh.length === 0 && pruned === 0) return prev;
        return [...kept, ...fresh];
      });
      console.info('[ChatSync] chat list synced', { fetched: chats.length, added, pruned });
    } catch (e) {
      // Offline or a failed request must not break the app: the local list stays as it is.
      console.warn('[ChatSync] could not load the chat list', e);
    } finally {
      chatSyncRef.current.inFlight = false;
    }
  }, [token, t]);

  useEffect(() => {
    if (!token || chatSyncRef.current.lastToken === token) return;
    chatSyncRef.current.lastToken = token;
    void syncChats();
  }, [token, syncChats]);

  // CHAT_SYNC_FOCUS: сверка при возвращении во вкладку. Debounce обязателен: пользователь, который
  // быстро переключается между окнами, иначе выдал бы по запросу на каждое переключение.
  const FOCUS_SYNC_MIN_INTERVAL_MS = 12_000;
  useEffect(() => {
    const onFocus = () => {
      if (Date.now() - chatSyncRef.current.lastStartedAt < FOCUS_SYNC_MIN_INTERVAL_MS) return;
      void syncChats();
    };
    const onVisibility = () => {
      // visibilitychange срабатывает и на скрытие вкладки — реагируем только на возвращение.
      if (document.visibilityState !== 'visible') return;
      onFocus();
    };

    window.addEventListener('focus', onFocus);
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      window.removeEventListener('focus', onFocus);
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [syncChats]);

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
          updateMessage(prev, ctx.sessionId, ctx.messageId, (m) => {
            // AGENT_TIMELINE: добавлено 2026-09-22 — каждая фаза «размышления» становится
            // отдельным шагом со своим startedAt, поэтому таймер каждого шага честно идёт
            // от нуля, а завершённые шаги больше не тикают и не путаются между собой.
            const steps = m.steps ?? [];
            const last = steps[steps.length - 1];
            const open = last && last.endedAt === undefined ? last : undefined;

            if (payload.stage === 'thinking') {
              // A new reasoning phase: close the previous one and start a fresh step.
              const now = Date.now();
              // AGENT_FEED_ZED: the reasoning stream is cumulative on the message, so each step
              // records the slice of it that belongs to that step.
              const thinkingLength = (m.thinking ?? '').length;
              const closed = open
                ? steps.map((s) =>
                    s.id === open.id ? { ...s, endedAt: now, reasoningTo: thinkingLength } : s,
                  )
                : steps;
              return {
                ...m,
                currentAction: payload,
                steps: [
                  ...closed,
                  {
                    id: uid(),
                    stage: payload.stage,
                    label: payload.label,
                    startedAt: now,
                    afterToolCount: (m.toolActions ?? []).length,
                    reasoningFrom: thinkingLength,
                  },
                ],
              };
            }

            // Any other phase ends the current reasoning step.
            if (!open) return { ...m, currentAction: payload };
            const now = Date.now();
            const thinkingLength = (m.thinking ?? '').length;
            return {
              ...m,
              currentAction: payload,
              steps: steps.map((s) =>
                s.id === open.id ? { ...s, endedAt: now, reasoningTo: thinkingLength } : s,
              ),
            };
          }),
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
        if (disposed) return;

        // TASK_COMPLETION_WATCHDOG: fall back to resolving the message by task id, so a finished run
        // can never leave the transcript stuck in "streaming".
        const ctx = streamingRef.current ?? findStreamingTarget(payload.taskId);
        if (!ctx) return;
        const { sessionId, messageId } = ctx;
        streamingRef.current = null;
        setAgentStatus('Completed');
        setSessions((prev) =>
          updateMessage(prev, sessionId, messageId, (m) => ({
            ...m,
            content: m.content || payload.result,
            status: 'complete',
            steps: closeSteps(m.steps),
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
        if (disposed) return;
        const ctx = streamingRef.current ?? findStreamingTarget();
        if (!ctx) return;
        const { sessionId, messageId } = ctx;
        streamingRef.current = null;
        setAgentStatus('Failed');
        setSessions((prev) =>
          updateMessage(prev, sessionId, messageId, (m) => ({
            ...m,
            status: 'error',
            error: err,
            steps: closeSteps(m.steps),
          })),
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
        if (disposed) return;
        const ctx = streamingRef.current ?? findStreamingTarget();
        if (!ctx) return;
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
      // SIGNALR_RESILIENCE: добавлено 2026-09-22 — после успешного переподключения события,
      // которые не дошли, уже не вернуть, поэтому состояние хода надо перечитать с сервера.
      onReconnected: () => {
        if (disposed) return;
        const ctx = streamingRef.current;
        if (!ctx?.taskId) return;
        void resyncTurn(ctx.sessionId, ctx.messageId, ctx.taskId);
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

  // INCOGNITO_CHAT: добавлено 2026-09-20
  // The switch is offered only for a still-empty plain chat: once the first message is sent the
  // mode is locked in (the header keeps a passive badge so the user cannot forget it).
  const chatTab = !isAgent && !students;
  const chatEmpty = (activeSession?.messages.length ?? 0) === 0;
  const incognitoActive = incognito && chatTab;
  const showIncognitoToggle = chatTab && chatEmpty && Boolean(token);
  // Incognito chats are in-memory only, so they never reach the sidebar or localStorage.
  const visibleSessions = sessions.filter((s) => !s.incognito);

  // CLAUDE_LAYOUT: добавлено 2026-09-20
  // Every tab gets the centred start screen while its conversation is empty; the greeting and
  // the chips are adapted to the tab, and guests get the auth block instead.
  const isGuest = !token && !initializing;
  const stageEmpty = chatEmpty && !initializing;
  // The compact header mark appears as soon as the conversation starts.
  const showHeaderLogo = chatTab && !chatEmpty;

  // TABS_UNIFIED: chips follow the tab. The chat tab offers real controls (model, smart
  // search); the agent tab shows passive hint chips instead — it runs autonomously and picks its
  // own reasoning depth. Students also shows hint chips, but its reasoning on/off switch is a real
  // control and lives in the model picker, like in the chat tab.
  const quickChips: ChatQuickChip[] = !token
    ? []
    : chatTab
      ? [
          {
            key: 'flash',
            label: t('chat.chipFlash'),
            onClick: () => handleModelChange('ConexyV1-flash'),
            active: model === 'ConexyV1-flash',
          },
          {
            key: 'pro',
            label: t('chat.chipPro'),
            onClick: () => handleModelChange('ConexyV1-pro'),
            active: model === 'ConexyV1-pro',
          },
          {
            key: 'search',
            label: t('chat.chipSmartSearch'),
            onClick: () => setSmartSearch((v) => !v),
            active: smartSearch,
          },
        ]
      : isAgent
        ? model === 'conexy-cowork'
          ? [
              { key: 'cowork-research', label: t('agent.coworkChipResearch') },
              { key: 'cowork-documents', label: t('agent.coworkChipDocuments') },
              { key: 'cowork-reports', label: t('agent.coworkChipReports') },
            ]
          : [
              { key: 'agent-autonomous', label: t('agent.chipAutonomous') },
              { key: 'agent-tools', label: t('agent.chipTools') },
              { key: 'agent-verify', label: t('agent.chipVerify') },
            ]
        : [
            { key: 'students-socratic', label: t('students.chipSocratic') },
            { key: 'students-topics', label: t('students.chipTopics') },
            { key: 'students-no-solutions', label: t('students.chipNoSolutions') },
          ];

  // LOGO_SWEEP: the start screen greets by time of day, next to the mark (Claude-style row).
  const displayName = user?.displayName ? user.displayName.split('@')[0] : null;
  const greeting = t(greetingKey(activeTab), { name: displayName ?? t('chat.defaultName') });

  // BUGFIX_PERF: добавлено 2026-09-21
  // MessageBubble is memoised, which only helps if the callbacks it receives keep one identity
  // for the whole session. The handlers below therefore read the mutable inputs through this
  // latest-values ref instead of closing over state that changes on every streamed token.
  const liveRef = useRef({} as {
    token: string | null;
    sessions: ChatSession[];
    activeId: string | null;
    activeTab: ChatSessionKind;
    model: ConexyModel;
    thinking: boolean;
    reasoningEffort: ReasoningEffort;
    smartSearch: boolean;
    students: boolean;
    incognitoActive: boolean;
    sessionIncognito: boolean;
    sessionTaskId: string | undefined;
    pendingAction: PendingActionPayload | null;
    showToast: (message: string) => void;
    refreshUsage: () => Promise<void>;
    t: typeof t;
  });
  liveRef.current = {
    token,
    sessions,
    activeId,
    activeTab,
    model,
    thinking,
    reasoningEffort,
    smartSearch,
    students,
    incognitoActive,
    sessionIncognito: activeSession?.incognito ?? false,
    sessionTaskId: activeSession?.taskId,
    pendingAction,
    showToast,
    refreshUsage,
    t,
  };

  // TASK_COMPLETION_WATCHDOG: добавлено 2026-09-22
  // Резолвер цели для терминальных событий. Раньше onCompleted/onError/onStopped просто выходили,
  // если streamingRef оказался пуст, и сообщение навсегда оставалось в состоянии «генерирует».
  // Теперь цель ищется по taskId (или по активной сессии), а не только по локальному контексту.
  function findStreamingTarget(taskId?: string): { sessionId: string; messageId: string } | null {
    const all = liveRef.current.sessions;
    const session =
      (taskId ? all.find((s) => s.taskId === taskId) : undefined) ??
      all.find((s) => s.id === liveRef.current.activeId);
    if (!session) return null;

    const message = [...session.messages]
      .reverse()
      .find((m) => m.role === 'assistant' && m.status === 'streaming');
    return message ? { sessionId: session.id, messageId: message.id } : null;
  }

  // Latest agent progress for the IDE bottom panel (the task checklist).
  const lastAssistant = [...(activeSession?.messages ?? [])].reverse().find((m) => m.role === 'assistant') ?? null;
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

  // BUGFIX_PERF: stable identity — it is a dependency of handleNewChat, which is passed to the
  // memoised MessageBubble.
  const handleTabChange = useCallback((next: ChatSessionKind) => {
    setActiveTab(next);
    // A top-level tab switch opens a fresh, empty composer for that mode — it must
    // never auto-restore the last-opened chat of the previous (or current) mode.
    // We set a draft id (not null) so a workspace chatId is already available if the
    // user creates files manually before ever sending a message to the agent.
    setActiveId(uid());
    // STUDENTS_REASONING: added 2026-09-23. Reasoning now has an explicit toggle in students mode
    // too, so the tab switch also picks its default. Students keeps reasoning ON by default — that
    // is the behaviour it had while the depth was hard-coded server-side, and making it explicit
    // keeps it discoverable instead of silently changing how the tutor answers. The chat tab keeps
    // the chat default (off).
    setThinking(next === 'students');
    setSmartSearch(false);
    setReasoningEffort('high');
    // INCOGNITO_CHAT: switching tabs leaves the incognito mode behind with the old chat.
    setIncognito(false);
    if (next === 'projects') setModel('conexy-coder');
    else if (next === 'students') setModel('ConexyV1-pro');
    else setModel('ConexyV1-flash');
  }, []);

  // NEW_CHAT_LOGO: добавлено 2026-09-20
  /** Starts a fresh conversation in the current tab (used by the mark under a reply). */
  const handleNewChat = useCallback(() => {
    handleTabChange(liveRef.current.activeTab);
  }, [handleTabChange]);

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
    // INCOGNITO_CHAT: a new chat always starts as a normal one.
    setIncognito(false);
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
      // INCOGNITO_CHAT: добавлено 2026-09-20
      incognito: incognitoActive,
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
      model: isAgentModel(model) ? model : 'conexy-coder',
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

  // CHAT_DELETE: добавлено 2026-09-23
  // Удаление теперь физическое: сначала сервер (история + рабочая область), и только потом
  // локальный список. Раньше чат исчезал только в этом браузере, а следующий sync возвращал его из
  // базы обратно.
  async function handleDeleteSession(id: string) {
    // Clear every reference to the deleted chat so it can never be re-opened:
    // the session list, the active id, and any in-flight stream still targeting
    // it (whose late SignalR deltas could otherwise re-attach messages).
    if (streamingRef.current?.sessionId === id) {
      streamingRef.current = null;
    }

    const session = sessionsRef.current.find((s) => s.id === id);
    // A chat that never reached the server (draft, incognito) has nothing to delete there.
    const shouldAskServer = Boolean(session) && !session!.incognito && GUID_LIKE.test(id);

    if (shouldAskServer) {
      try {
        await deleteChat(id);
      } catch (e) {
        const status = (e as { response?: { status?: number } })?.response?.status;
        // 404 means the server has no such chat for this user: it is safe (and correct) to drop the
        // local copy anyway. Any other failure must NOT pretend the delete happened.
        if (status !== 404) {
          console.warn('[ChatDelete] could not delete the chat on the server', { id, status, error: String(e) });
          showToast(t('chat.deleteFailed'));
          return;
        }
      }
    }

    setSessions((prev) => prev.filter((s) => s.id !== id));
    if (activeId === id) {
      setActiveId(uid());
      // INCOGNITO_CHAT: the deleted chat took its mode with it.
      setIncognito(false);
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

  // BUGFIX_PERF: stable identity (see liveRef above). Everything mutable is read from the ref so
  // the memo on MessageBubble survives per-token session updates.
  const startCompletion = useCallback(async (
    sessionId: string,
    prompt: string,
    attachments: TaskAttachment[],
    appendUserMessage: boolean,
    // CONTINUE_GENERATION: добавлено 2026-09-21 — when set, the answer resumes inside that
    // existing message instead of appending a new user/assistant pair.
    continueMessageId?: string,
  ): Promise<SendOutcome> => {
    const live = liveRef.current;
    setAgentStatus('Working…');
    const session = live.sessions.find((s) => s.id === sessionId);
    const kind = session?.kind ?? live.activeTab;
    const currentTaskId = session?.taskId;
    const continueFrom = continueMessageId
      ? session?.messages.find((m) => m.id === continueMessageId)?.content
      : undefined;

    // ATTACHMENTS_IN_BUBBLE: turn the outgoing files into the transcript representation (image
    // thumbnails, file chips). Only the preview is kept — the originals go to the model.
    const messageAttachments = attachments.length
      ? await Promise.all(attachments.map((a) => toMessageAttachment(a)))
      : [];

    const userMsg: ChatMessage = {
      id: uid(),
      role: 'user',
      content: prompt,
      thinking: '',
      status: 'complete',
      logs: [],
      screenshots: [],
      createdAt: Date.now(),
      model: live.model,
      attachments: messageAttachments.length ? messageAttachments : undefined,
    };
    const assistantMsg: ChatMessage = {
      id: continueMessageId ?? uid(),
      role: 'assistant',
      content: '',
      thinking: '',
      status: 'streaming',
      logs: [],
      screenshots: [],
      createdAt: Date.now(),
      model: live.model,
    };

    setSessions((prev) =>
      updateSession(prev, sessionId, (s) => ({
        ...s,
        title: s.title === defaultTitle(kind, live.t) ? prompt.slice(0, 40) : s.title,
        status: 'Running',
        model: live.model,
        messages: continueMessageId
          ? s.messages.map((m) =>
              m.id === continueMessageId ? { ...m, status: 'streaming', error: undefined } : m,
            )
          : appendUserMessage
            ? [...s.messages, userMsg, assistantMsg]
            : [...s.messages, assistantMsg],
      })),
    );

    try {
      const res = await runTask({
        model: live.model,
        prompt,
        attachments: attachments.length ? attachments : undefined,
        thinking: live.thinking,
        reasoningEffort: live.reasoningEffort,
        smartSearch: live.smartSearch,
        studentsMode: live.students,
        sessionId: currentTaskId,
        chatId: sessionId,
        // INCOGNITO_CHAT: добавлено 2026-09-20 — 'incognitoActive' covers the very first
        // message (the session does not exist yet); later turns read the flag off the session.
        incognito: live.incognitoActive || live.sessionIncognito,
        // CONTINUE_GENERATION: the partial answer is handed back so the model finishes it.
        assistantPrefix: continueFrom?.trim() ? continueFrom : undefined,
        // CHAT_KIND_SYNC: режим вкладки сохраняется вместе с историей, чтобы на другом устройстве
        // чат учеников открылся в «Учениках», а не в общем чате.
        chatKind: live.sessions.find((s) => s.id === sessionId)?.kind ?? live.activeTab,
      });

      setSessions((prev) => updateSession(prev, sessionId, (s) => ({ ...s, taskId: res.id, remote: true })));
      streamingRef.current = { sessionId, messageId: assistantMsg.id, taskId: res.id };
      // AGENT_EVENT_GROUPS: добавлено 2026-09-22 — бэкенд вещает в ДВЕ разные группы:
      //   * task_{taskId}  — раннер и воркер (OnAgentStatus, pending_confirmation, OnCompleted…);
      //   * task_{chatId}  — сервисы bash и редактора (started/completed/failed, TerminalOutput,
      //                      BuildProblems), потому что для них этот id — ещё и ключ воркспейса.
      // Мы слушали только первую, поэтому карточка команды создавалась по pending_confirmation,
      // а событие о завершении уходило в пустоту — статус навсегда застревал на «выполняется»,
      // и строки правок файлов тоже не появлялись. Подписываемся на обе.
      // DEPLOY_WINDOW_GRACEFUL_ERRORS: подписка на группы не должна ронять отправку. Задача на
      // бэкенде УЖЕ принята, а событий мы не увидим только до того, как связь вернётся — сторож
      // (resyncTurn → ensureGroup) дозальёт группы сам. Раньше ошибка joinTask превращала успешно
      // принятую задачу в «Ошибку» в UI.
      await signalrService.joinTask(res.id).catch((e: unknown) => {
        console.warn('[signalr] joinTask after run failed; the watchdog will retry', { taskId: res.id, error: String(e) });
      });
      await signalrService.joinTask(sessionId).catch((e: unknown) => {
        console.warn('[signalr] joinTask for the workspace failed; the watchdog will retry', { sessionId, error: String(e) });
      });
      return { ok: true };
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
              m.id === assistantMsg.id ? { ...m, status: 'error', error: live.t('agent.limitExceeded') } : m,
            ),
          })),
        );
        void live.refreshUsage();
        return { ok: false };
      }

      // ATTACHMENT_SIZE_LIMIT: добавлено 2026-09-22 — прокси (nginx, дефолт 1MB) режет тело
      // раньше бэкенда и отдаёт голый 413. Это не ошибка модели, а неудавшаяся отправка,
      // поэтому оптимистичные сообщения убираем из ленты, а текст и файлы возвращает композер.
      const tooLarge = (e as { response?: { status?: number } })?.response?.status === 413;

      const message = humanError(e, live.t);
      streamingRef.current = null;

      if (tooLarge && appendUserMessage) {
        setAgentStatus('Ready');
        setSessions((prev) =>
          updateSession(prev, sessionId, (s) => ({
            ...s,
            status: 'Idle',
            messages: s.messages.filter((m) => m.id !== userMsg.id && m.id !== assistantMsg.id),
          })),
        );
        return { ok: false, tooLarge: true };
      }

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
      return { ok: false };
    }
  }, []);

  async function handleSend(prompt: string, attachments: TaskAttachment[]): Promise<SendOutcome> {
    if (!prompt.trim() || !token) return { ok: false };
    const sessionId = ensureSessionId(activeTab);
    return startCompletion(sessionId, prompt, attachments, true);
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

  // BUGFIX_PERF: stable callbacks (see liveRef) so MessageBubble's memo is not defeated.
  const handleRegenerate = useCallback((assistantMessageId: string) => {
    const live = liveRef.current;
    if (!live.token || streamingRef.current) return;
    const session = live.sessions.find((s) => s.id === live.activeId);
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
  }, [startCompletion]);

  const handleResend = useCallback((messageId: string) => {
    const live = liveRef.current;
    if (!live.token || streamingRef.current) return;
    const session = live.sessions.find((s) => s.id === live.activeId);
    if (!session) return;
    const message = session.messages.find((m) => m.id === messageId);
    if (!message || message.role !== 'user') return;
    void startCompletion(session.id, message.content, [], true);
  }, [startCompletion]);

  const handleEditMessage = useCallback((messageId: string, newContent: string) => {
    const live = liveRef.current;
    if (!live.token || streamingRef.current) return;
    const session = live.sessions.find((s) => s.id === live.activeId);
    if (!session) return;
    const message = session.messages.find((m) => m.id === messageId);
    if (!message || message.role !== 'user') return;
    void startCompletion(session.id, newContent, [], true);
  }, [startCompletion]);

  // CONTINUE_GENERATION: добавлено 2026-09-21
  // BUGFIX_CONTINUE_CLICK: добавлено 2026-09-22 — раньше здесь были «тихие» return'ы (нет
  // токена, идёт стрим, пустой частичный ответ), из-за которых клик по «Продолжить» выглядел
  // как полностью мёртвая кнопка. Теперь каждый отказ либо логичен, либо виден пользователю.
  // Resumes a stopped answer: the already-streamed text goes back to the model as its own
  // truncated turn, and the reply keeps growing inside the very same message.
  const handleContinue = useCallback((messageId: string) => {
    const live = liveRef.current;
    if (!live.token) return;

    if (streamingRef.current) {
      // Something is already running — say so instead of swallowing the click.
      live.showToast(live.t('message.continueBusy'));
      return;
    }

    // Prefer the session currently on screen, then fall back to whichever session owns it.
    const session =
      live.sessions.find(
        (s) => s.id === live.activeId && s.messages.some((m) => m.id === messageId),
      ) ?? live.sessions.find((s) => s.messages.some((m) => m.id === messageId));
    if (!session) return;

    const index = session.messages.findIndex((m) => m.id === messageId);
    const target = session.messages[index];
    if (!target || target.role !== 'assistant') return;

    // The prompt this answer belongs to.
    let prompt = '';
    for (let i = index - 1; i >= 0; i--) {
      const m = session.messages[i];
      if (m.role === 'user') {
        prompt = m.content;
        break;
      }
    }
    if (!prompt.trim()) {
      live.showToast(live.t('message.continueNoPrompt'));
      return;
    }

    // An answer stopped before the first token has nothing to resume from; the call is still made
    // with an empty prefix, so the model simply answers the prompt again inside the same message
    // instead of the button doing nothing at all.
    void startCompletion(session.id, prompt, [], false, messageId);
  }, [startCompletion]);

  function finalizeStopped(ctx: { sessionId: string; messageId: string }) {
    setAgentStatus('Stopped');
    // CONTINUE_GENERATION: the stop marker is no longer baked into the text — it is rendered
    // from the 'stopped' status, so the stored content stays exactly the partial answer that
    // "Продолжить" hands back to the model.
    setSessions((prev) =>
      updateMessage(prev, ctx.sessionId, ctx.messageId, (m) => {
        const steps = closeSteps(m.steps);
        if (m.status === 'stopped') return { ...m, steps };
        return { ...m, status: 'stopped', steps };
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

  // AGENT_FEED_ZED: добавлено 2026-09-23 — клик по пути файла в строке действия агента.
  // BUGFIX_PERF: stable identity so MessageBubble's memo holds.
  const handleOpenWorkspaceFile = useCallback((path: string) => {
    openFileNonceRef.current += 1;
    setOpenFileRequest({ path, nonce: openFileNonceRef.current });
  }, []);

  // COMMAND_CONFIRM: добавлено 2026-09-20
  // CONFIRM_GATE: добавлено 2026-09-22 — сервер теперь отвечает `false`, если ожидающего решения
  // уже нет (гейт сломался или задача завершилась). Это больше не тихий no-op: пользователь видит,
  // что решение не применилось, и не остаётся в уверенности, что команда была одобрена.
  // BUGFIX_PERF: stable identity so MessageBubble's memo holds.
  const handleCommandDecision = useCallback(async (actionId: string, approved: boolean, allowAll: boolean) => {
    const live = liveRef.current;
    const taskId = live.pendingAction?.taskId ?? streamingRef.current?.taskId ?? live.sessionTaskId;
    setPendingAction(null);
    if (approved && allowAll && taskId) setAllowAllTaskId(taskId);
    try {
      const delivered = await signalrService.confirmAction(actionId, approved, allowAll);
      if (!delivered) {
        if (approved && allowAll) setAllowAllTaskId(null);
        live.showToast(live.t('toast.confirmExpired'));
      }
      return delivered;
    } catch (err) {
      console.error('[CommandConfirm] ConfirmAction failed:', err);
      if (approved && allowAll) setAllowAllTaskId(null);
      live.showToast(live.t('toast.confirmFailed'));
      return false;
    }
  }, []);

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
  // ADMIN_PANEL_FIX: добавлено 2026-09-23 — сначала отдаём явный исход для «профиль неизвестен»
  // и «нет прав», и только потом саму панель.
  if (adminView) {
    return adminView;
  }

  // ADMIN_ERROR_BOUNDARY: добавлено 2026-09-23 — на самой панели рендер отделён от приложения,
  // поэтому любая ошибка внутри даёт читаемый экран с выходом, а не пустой чёрный фон.
  if (isAdminRoute && user?.isAdmin) {
    return (
      <ErrorBoundary backLabel={t('admin.backToChat')} onBack={() => { window.location.hash = ''; }}>
        <AdminPanel onBack={() => { window.location.hash = ''; }} onToast={showToast} />
      </ErrorBoundary>
    );
  }

  // INCOGNITO_CHAT: добавлено 2026-09-20
  // Rendered in the chat header: an active switch while the chat is still empty, a passive
  // badge afterwards (the mode is locked, but the user must not forget it is on).
  const incognitoControl = showIncognitoToggle ? (
    <button
      className={`incognito-toggle ${incognito ? 'incognito-toggle--on' : ''}`}
      onClick={() => setIncognito((v) => !v)}
      type="button"
      aria-pressed={incognito}
      title={t('chat.incognitoHint')}
    >
      <GhostIcon size={15} />
      <span>{t('chat.incognito')}</span>
    </button>
  ) : incognitoActive ? (
    <span className="incognito-badge" title={t('chat.incognitoHint')}>
      <GhostIcon size={15} />
      <span>{t('chat.incognito')}</span>
    </span>
  ) : null;

  // INCOGNITO_AURA: добавлено 2026-09-20
  // The incognito look is document-level state, so the amber aura can sit behind everything
  // (sidebar included) with the theme variables switching to the black/yellow palette.
  useEffect(() => {
    const root = document.documentElement;
    if (incognitoActive) root.setAttribute('data-incognito', 'true');
    else root.removeAttribute('data-incognito');
    return () => root.removeAttribute('data-incognito');
  }, [incognitoActive]);

  return (
    <>
      {incognitoActive && <div className="incognito-aura" aria-hidden="true" />}
      <ConnectionBanner />
      <div className="app">
      <Sidebar
        open={sidebarOpen}
        activeTab={activeTab}
        search={search}
        sessions={visibleSessions}
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
          // COWORK_MODE: an agent chat reopens in the mode it was run in (Coder or Cowork) —
          // otherwise the next message would silently go to whichever mode the picker showed last.
          const selected = sessions.find((s) => s.id === id);
          if (selected && selected.kind === 'projects' && isAgentModel(selected.model)) {
            setModel(selected.model);
          }
          // INCOGNITO_CHAT: opening another chat always returns to normal mode.
          setIncognito(false);
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
                <div className="chat-header__left">
                  {showHeaderLogo && (
                    <span className="chat-header__logo">
                      {/* LOGO_SWEEP: the header mark stays static — the sweep only runs inside
                          the message flow, under the running tool pills. */}
                      <ConexyLogo size={32} />
                    </span>
                  )}
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
                </div>
                {isAgent ? (
                  <div className="chat-header__actions">
                    {/* SUBSCRIPTION_TIERS: добавлено 2026-09-17 */}
                    <UsageIndicator usage={usage} />
                  </div>
                ) : (
                  incognitoControl
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
            {/* INCOGNITO_CHAT / LOGO_SWEEP: desktop chat header. Holds the compact mark that the
                start-screen logo collapses into, plus the incognito control. */}
            {!isMobile && !isAgent && !students && (
              <div className="chat-header chat-header--chat">
                <div className="chat-header__left">
                  {showHeaderLogo && (
                    <span className="chat-header__logo">
                      <ConexyLogo size={32} />
                    </span>
                  )}
                </div>
                {incognitoControl}
              </div>
            )}
            <ChatLayout
              empty={stageEmpty}
              hero={
                isGuest ? (
                  <GuestHero
                    mode={isAgent ? 'agent' : students ? 'students' : 'chat'}
                    onLogin={() => setAuthModal('login')}
                    onRegister={() => setAuthModal('register')}
                  />
                ) : (
                  <div className="chat-hero-row">
                    <ConexyLogo size={36} />
                    <span className="chat-hero-greeting">{greeting}</span>
                  </div>
                )
              }
              chips={quickChips}
              feed={
                token ? (
                  <ChatFeed
                    session={activeSession}
                    nickname={user?.displayName ? user.displayName.split('@')[0] : null}
                    onRegenerate={handleRegenerate}
                    onResend={handleResend}
                    onEditMessage={handleEditMessage}
                    onCommandDecision={handleCommandDecision}
                    onNewChat={handleNewChat}
                    onContinue={handleContinue}
                    onOpenFile={handleOpenWorkspaceFile}
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
                )
              }
              composer={
                <>
                  {/* COMMAND_CONFIRM: добавлено 2026-09-20 — while auto-approve is on, agent
                      commands run without asking; this is the single switch-off point. */}
                  {allowAllTaskId && (
                    <div className="agent-autoapprove">
                      <span className="agent-autoapprove__text">{t('cmdConfirm.allowAllActive')}</span>
                      <button
                        className="agent-autoapprove__off"
                        onClick={() => void handleDisableAllowAll()}
                        type="button"
                      >
                        {t('cmdConfirm.disableAllowAll')}
                      </button>
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
                </>
              }
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
                openFileRequest={openFileRequest}
                fileRefreshToken={fileRefreshToken}
                agentFileChange={agentFileChange}
                todos={latestTodos}
                onCursorChange={setCursorInfo}
                onRunInSeparateWindow={() => showToast(t('toast.runProject'))}
                hideRun={model === 'conexy-cowork'}
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
    </>
  );
}
