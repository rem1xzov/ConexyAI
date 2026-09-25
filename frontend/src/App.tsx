import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { setAuthToken } from './api/client';
import { getChats, getChat, getChatTranscript, deleteChat, renameChat, setChatPinned, getSubscriptionUsage, getTaskStatus, runTask } from './api/conexyApi';
import { isForbiddenJoinError, signalrService } from './services/signalrService';
// TURN_SCOPE: добавлено 2026-09-24 (H6/H7)
import { TurnRegistry, type TurnContext } from './services/turnRegistry';
import { useAuth } from './hooks/useAuth';
import { useIsMobile } from './hooks/useMediaQuery';
// FILE_DROP: добавлено 2026-09-24 (L11)
import { useFileDropZone, usePreventWindowFileDrop } from './hooks/useFileDrop';
// MOBILE_DRAWER / MOBILE_KEYBOARD: добавлено 2026-09-24
import { useDrawerSwipe } from './hooks/useDrawerSwipe';
import { useKeyboardInset } from './hooks/useKeyboardInset';
import { Sidebar } from './components/Sidebar';
import { ChatFeed } from './components/ChatFeed';
import { InputBar } from './components/InputBar';
import { ModelPicker } from './components/ModelPicker';
import { WorkspacePanel } from './components/WorkspacePanel';
import { StatusBar, type AgentStatus } from './components/StatusBar';
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
// WORKSPACE_DIRTY: добавлено 2026-09-24
import { ConfirmDialog } from './components/Dialog';
import { getStoredTheme, setTheme, type Theme } from './theme';
import { setLanguage } from './i18n';
import type { ChatSummary, ConexyModel, LimitExceededInfo, ReasoningEffort, SendOutcome, SubscriptionUsage, TaskAttachment } from './types/api';
import type { ChatMessage, ChatSession, ChatSessionKind, AgentStep } from './types/chat';
import type { PendingActionPayload, SignalrCallbacks } from './types/signalr';
// ATTACHMENTS_IN_BUBBLE: добавлено 2026-09-21
import { toMessageAttachment } from './utils/attachments';
// DEPLOY_WINDOW_GRACEFUL_ERRORS: добавлено 2026-09-23
import { humanError } from './utils/humanError';
// SESSION_ISOLATION / CHAT_STORAGE: добавлено 2026-09-24 (H10, M22, L13)
import { userIdFromToken } from './utils/authToken';
import {
  ChatPersistence,
  chatIdFromStorageKey,
  clearUserSessions,
  loadUserSessions,
  parseStoredSession,
  takeLegacySessions,
} from './utils/chatStorage';
import {
  isAgentModel,
  isSessionStreaming,
  kindFromServer,
  mapLimit,
  mergeTranscript,
  messagesFromTranscript,
  modelFromServer,
  sessionFromServer,
  uid,
} from './utils/chatSession';

// CHAT_DELETE: маршрут удаления объявлен как {chatId:guid}, поэтому запрос на не-UUID id чата
// смысла не имеет (старые локальные сессии).
const GUID_LIKE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

// SMOOTH_STREAM: tokens arrive in network-sized bursts, so writing each one straight into state made
// the answer grow in visible steps. They are queued and released a few characters per frame instead.
// 2-4 characters is the calm, steady pace; a backlog above the threshold is flushed faster so
// smoothing never holds a fast answer back.
const MIN_TOKEN_CHARS_PER_FRAME = 2;
const MAX_TOKEN_CHARS_PER_FRAME = 4;
const TOKEN_BACKLOG_ACCELERATE = 40;
const FAST_TOKEN_CHARS_PER_FRAME = 16;

// CHAT_LIST_COMPLETE: добавлено 2026-09-24 (H8, C-3) — сколько последних чатов просить у сервера.
// Полный набор id приходит отдельно (allChatIds), поэтому лимит больше не определяет, что удалять.
const CHAT_LIST_LIMIT = 200;
// Сколько новых (незнакомых этому устройству) чатов загружать сразу вместе с перепиской; остальные
// догружаются при открытии — первая синхронизация на новом устройстве не делает 200 запросов.
const EAGER_TRANSCRIPTS = 10;
const TRANSCRIPT_CONCURRENCY = 4;
// SESSION_ISOLATION: токен без читаемого `sub` (не должно случаться) всё равно получает свой ключ.
const UNKNOWN_USER = 'unknown';

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

// CHAT_SHARE: `#/chat/<id>` — ссылка, которую выдаёт «Поделиться». Это hash-маршрут, поэтому он не
// требует правил переписывания на сервере, и открыть чат по нему может только его владелец:
// история читается строго по UserId, у остальных просто нет такого чата.
function chatIdFromHash(hash: string): string | null {
  const match = /^#\/chat\/([0-9a-fA-F-]{36})$/.exec(hash);
  return match ? match[1] : null;
}

function chatLink(id: string): string {
  return `${window.location.origin}${window.location.pathname}#/chat/${id}`;
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

// TURN_SCOPE: добавлено 2026-09-24
/** Where a turn's final state is written: its chat, its assistant message and (if known) its task. */
interface TurnTarget {
  sessionId: string;
  messageId: string;
  taskId?: string;
}

type TurnOutcome = 'complete' | 'stopped' | 'error';

interface FinalizeOptions {
  /** Full stored answer (OnCompleted payload / task record). */
  result?: string;
  error?: string;
  /** Also rewrite a message the user already stopped locally (the server knows better). */
  allowFromStopped?: boolean;
}

function httpStatus(e: unknown): number | undefined {
  return (e as { response?: { status?: number } } | null)?.response?.status;
}

function errorCode(e: unknown): string | undefined {
  const data = (e as { response?: { data?: { error?: unknown } } } | null)?.response?.data;
  return typeof data?.error === 'string' ? data.error : undefined;
}

// REGENERATE_REPLACES_TURN: добавлено 2026-09-24 (C-10)
/** True when this answer's turn is stored on the server (accepted by `POST /run`, or synced from it). */
function turnPersisted(answer: ChatMessage | undefined): boolean {
  if (!answer || answer.role !== 'assistant') return false;
  if (answer.taskId) return true;
  // No task id: either a transcript row from the server (stored) or a send that never got through.
  return !answer.awaitingTaskId && answer.status !== 'error';
}

/**
 * Whether re-sending the user message at `userIndex` replaces the chat's LAST stored turn (C-10).
 * Only then may the request carry `regenerate: true` — for anything else the server would delete a
 * turn that has nothing to do with this one.
 */
function replacesLastTurn(messages: ChatMessage[], userIndex: number): boolean {
  if (userIndex < 0 || messages.slice(userIndex + 1).some((m) => m.role === 'user')) return false;
  const last = messages[messages.length - 1];
  return messages.length - 1 > userIndex && turnPersisted(last);
}

export default function App() {
  // EMAIL_AUTH: добавлено 2026-09-19
  const { token, user, initializing, profileFailed, reloadProfile, error: authError, login, register, logout } = useAuth();
  // SETTINGS: добавлено 2026-09-19
  const { t, i18n } = useTranslation();
  // MOBILE: добавлено 2026-09-19 — drives the responsive layout (sidebar overlay, agent tabs).
  const isMobile = useIsMobile();
  // SESSION_ISOLATION: добавлено 2026-09-24 (H10) — чей это экран. Обновление токена того же
  // пользователя его не меняет; вход другого пользователя (или выход) — меняет.
  const userId = token ? (userIdFromToken(token) ?? UNKNOWN_USER) : null;

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
  // FOCUS_MODE: the start screen steps aside while the composer has focus (mobile layout only).
  const [composerFocused, setComposerFocused] = useState(false);
  const [workspaceWidth, setWorkspaceWidth] = useState(55); // % width of the IDE pane
  const [ideCollapsed, setIdeCollapsed] = useState(false);

  // SESSION_ISOLATION: чаты в памяти принадлежат `storeUser`; кеш на диске разложен по пользователям.
  const [storeUser, setStoreUser] = useState<string | null>(userId);
  const [sessions, setSessions] = useState<ChatSession[]>(() => loadUserSessions(userId));
  // Every visit starts on a fresh, empty chat with the default model instead of restoring
  // the last opened session. Previous chats stay available from the sidebar.
  const [activeId, setActiveId] = useState<string | null>(() => uid());
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
  // LOCAL_STORAGE_BUDGET: добавлено 2026-09-24 — переписка открытого чата догружается с сервера.
  const [transcriptLoad, setTranscriptLoad] = useState<{ id: string; failed: boolean } | null>(null);
  // FILE_DROP: добавлено 2026-09-24 (L11) — файлы, брошенные на колонку чата, уходят в композер.
  const [droppedFiles, setDroppedFiles] = useState<{ files: File[]; nonce: number } | null>(null);
  const dropNonceRef = useRef(0);
  // WORKSPACE_DIRTY: добавлено 2026-09-24 — в редакторе рабочей области есть несохранённые вкладки;
  // смена/создание/удаление открытого чата сначала спрашивает подтверждение.
  const [workspaceDirty, setWorkspaceDirty] = useState(false);
  const [pendingLeave, setPendingLeave] = useState<(() => void) | null>(null);
  // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
  // const [isLiveOpen, setIsLiveOpen] = useState(false);

  // SESSION_ISOLATION: добавлено 2026-09-24 (H10) — смена аккаунта (или выход) подменяет список
  // чатов ПРЯМО В РЕНДЕРЕ, до коммита: ни один кадр не показывает чаты прошлого пользователя, и
  // открытый чат не может «переехать» в чужой аккаунт. Побочные эффекты (чистка диска, хаба,
  // контекстов ходов) — в эффекте ниже.
  if (storeUser !== userId) {
    setStoreUser(userId);
    setSessions(loadUserSessions(userId));
    setActiveId(uid());
    setIncognito(false);
    setPendingAction(null);
    setAllowAllTaskId(null);
    setTranscriptLoad(null);
  }

  // CHAT_SYNC: добавлено 2026-09-23 — снимок списка чатов для синхронизации с сервером.
  const sessionsRef = useRef(sessions);
  sessionsRef.current = sessions;
  // CHAT_DELETE: тот же приём для активного чата — синхронизация должна знать, какой чат сейчас
  // открыт, чтобы не выдернуть его из-под пользователя при прунинге.
  const activeIdRef = useRef(activeId);
  activeIdRef.current = activeId;
  const storeUserRef = useRef(storeUser);
  storeUserRef.current = storeUser;
  const tokenRef = useRef(token);
  tokenRef.current = token;

  // SESSION_ISOLATION: обновлённый токен того же пользователя просто передаётся клиентам — без
  // переподключения хаба (раньше каждое обновление рвало соединение вместе с подписками хода).
  // Первый эффект компонента: всё, что ниже ходит в API, уже видит актуальный токен.
  useEffect(() => {
    setAuthToken(token);
    signalrService.setToken(token);
  }, [token]);

  // TURN_SCOPE: добавлено 2026-09-24 (H6/H7) — вместо одного глобального streamingRef: по контексту
  // на каждый идущий ход, с ключом taskId (и индексом по чату).
  const registryRef = useRef<TurnRegistry | null>(null);
  if (!registryRef.current) registryRef.current = new TurnRegistry();
  const registry = registryRef.current;
  // TASK_COMPLETION_DIAGNOSTICS: добавлено 2026-09-23 — номер тика сторожа для логов.
  const watchdogTickRef = useRef(0);

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

  // SESSION_ISOLATION: всё локальное состояние аккаунта чистится по смене userId (см. эффект ниже),
  // поэтому выход — это просто сброс токена.
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
  // ADMIN_GUEST: изменено 2026-09-24 (L13) — гость видит приглашение войти, а не вечную «Загрузку…»,
  // и окончательно упавший /auth/me показывает ошибку с повтором.
  const adminView = (() => {
    if (!isAdminRoute) return null;

    const backToChat = (
      <button className="admin-btn" type="button" onClick={() => { window.location.hash = ''; }}>
        {t('admin.backToChat')}
      </button>
    );

    if (!user) {
      if (!token && !initializing) {
        return (
          <div className="route-fallback">
            <p className="route-fallback__text">{t('sync.adminSignIn')}</p>
            <button className="admin-btn" type="button" onClick={() => setAuthModal('login')}>
              {t('sidebar.login')}
            </button>
            {backToChat}
            {authModal && (
              <AuthModal
                mode={authModal}
                onSubmit={handleAuthSubmit}
                onSwitchMode={() => setAuthModal(authModal === 'login' ? 'register' : 'login')}
                onClose={() => setAuthModal(null)}
              />
            )}
          </div>
        );
      }

      if (profileFailed) {
        return (
          <div className="route-fallback">
            <p className="route-fallback__text">{t('sync.profileFailed')}</p>
            <button className="admin-btn" type="button" onClick={reloadProfile}>
              {t('sync.retry')}
            </button>
            {backToChat}
          </div>
        );
      }

      return (
        <div className="route-fallback">
          <span className="route-fallback__spinner" aria-hidden="true" />
          <p className="route-fallback__text">{t('common.loading')}</p>
          {backToChat}
        </div>
      );
    }

    if (!user.isAdmin) {
      return (
        <div className="route-fallback">
          <p className="route-fallback__text">{t('admin.noAccess')}</p>
          {backToChat}
        </div>
      );
    }

    return null; // admin and authenticated -> the panel is rendered below
  })();

  // CHAT_SYNC: добавлено 2026-09-23
  // Список чатов раньше жил только в localStorage этого браузера — отсюда «разные чаты» на ПК и
  // телефоне под одним аккаунтом. Теперь список сверяется с сервером, и чаты, которых локально нет
  // (созданные на другом устройстве), подтягиваются вместе с историей.
  // Существующие локальные чаты НЕ перезаписываются: в них больше состояния (шаги агента, вложения,
  // todo), и они свежее, чем то, что успело доехать до истории.
  // CHAT_SYNC_FOCUS: добавлено 2026-09-23 — сверка вызывается не только при появлении токена, но и
  // при возвращении пользователя во вкладку (focus/visibilitychange), с debounce ниже.
  const chatSyncRef = useRef({ inFlight: false, lastStartedAt: 0, lastToken: null as string | null });
  // CHAT_RENAME: имена, переименованные локально, вместе со временем. Ответ синхронизации, ушедший
  // на сервер ДО переименования, содержит старое имя — без этой защиты он откатил бы только что
  // введённое название обратно.
  const recentRenamesRef = useRef(new Map<string, { title: string; at: number }>());
  // CHAT_PIN: то же самое для закрепления — иначе ответ синхронизации, ушедший до переключения,
  // вернул бы старый флаг и чат «отклеился» бы на глазах.
  const recentPinsRef = useRef(new Map<string, { isPinned: boolean; at: number }>());
  // CHAT_PIN_PENDING: закрепления, которые сервер ещё не знает (у чата нет ни одной строки истории:
  // `remote` ставится сразу после отправки задачи, а история пишется в конце хода). Такой пин
  // остаётся локальным и повторяется при следующей сверке — иначе сервер вернул бы false и
  // закрепление молча пропадало бы.
  const pendingPinsRef = useRef(new Map<string, boolean>());
  // LOCAL_STORAGE_BUDGET: чаты, чья переписка прямо сейчас грузится с сервера.
  const transcriptLoadingRef = useRef(new Set<string>());
  // CHAT_SHARE: см. эффект открытия ссылки ниже.
  const openedLinkRef = useRef<string | null>(null);
  // CHAT_SHARE_LINK: id чата, который уже запрашивали у сервера по ссылке (один запрос на ссылку).
  const linkFetchRef = useRef<string | null>(null);
  // TURN_SCOPE (H7): для какого пользователя уже подхвачены ходы, оставшиеся после перезагрузки.
  const adoptedForRef = useRef<string | null>(null);

  // CHAT_STORAGE: добавлено 2026-09-24 (M22, L13) — кеш чатов на диске: по ключу на чат, только
  // изменённые, не чаще раза в 500 мс (раньше — весь список на КАЖДЫЙ токен стрима).
  const persistenceRef = useRef<ChatPersistence | null>(null);
  useEffect(() => {
    if (!storeUser) return;
    const persistence = new ChatPersistence(storeUser, (chatId) =>
      chatId === activeIdRef.current ||
      Boolean(registry.bySession(chatId)) ||
      isSessionStreaming(sessionsRef.current.find((s) => s.id === chatId)),
    );
    persistence.seed(sessionsRef.current);
    persistenceRef.current = persistence;
    return () => {
      persistence.dispose(true);
      if (persistenceRef.current === persistence) persistenceRef.current = null;
    };
  }, [storeUser, registry]);

  useEffect(() => {
    // INCOGNITO_CHAT: добавлено 2026-09-20 — incognito chats stay in memory for the current
    // visit only; the persistence layer never writes them.
    const persistence = persistenceRef.current;
    if (!persistence || persistence.userId !== storeUser) return;
    persistence.schedule(sessions);
  }, [sessions, storeUser]);

  // TWO_TABS: добавлено 2026-09-24 (L13) — другая вкладка того же пользователя изменила чат: берём её
  // версию. Раньше каждая вкладка записывала свой снимок ВСЕГО списка и затирала чужие изменения.
  // Чат, в котором эта вкладка сама ведёт генерацию, не трогаем: её копия свежее.
  useEffect(() => {
    if (!storeUser) return;
    const onStorage = (e: StorageEvent) => {
      if (e.storageArea !== window.localStorage) return;
      const chatId = chatIdFromStorageKey(e.key, storeUser);
      if (!chatId || registry.bySession(chatId)) return;
      const incoming = parseStoredSession(e.newValue);
      setSessions((prev) => {
        const index = prev.findIndex((s) => s.id === chatId);
        if (!incoming) {
          // Removed in the other tab (deleted or pruned). The chat open here stays on screen.
          if (index < 0 || chatId === activeIdRef.current) return prev;
          persistenceRef.current?.acknowledge(chatId, null);
          return prev.filter((s) => s.id !== chatId);
        }
        const current = index >= 0 ? prev[index] : undefined;
        // The other tab only evicted its cached messages to save space; ours are still good.
        const next = current && incoming.needsTranscript && current.messages.length > 0
          ? { ...incoming, messages: current.messages, needsTranscript: current.needsTranscript, createdAt: current.createdAt }
          : incoming;
        persistenceRef.current?.acknowledge(chatId, next);
        if (index < 0) return [next, ...prev];
        const copy = prev.slice();
        copy[index] = next;
        return copy;
      });
    };
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
  }, [storeUser, registry]);

  // SESSION_ISOLATION: добавлено 2026-09-24 (H10) — выход или вход другого пользователя. Раньше
  // logout чистил только токен: следующий человек за этим браузером видел чужие чаты и мог
  // продолжить чужой агентский чат (в чужой рабочей области).
  const prevUserRef = useRef(userId);
  useEffect(() => {
    const previous = prevUserRef.current;
    if (previous === userId) return;
    prevUserRef.current = userId;

    registry.clear();
    recentRenamesRef.current.clear();
    recentPinsRef.current.clear();
    pendingPinsRef.current.clear();
    transcriptLoadingRef.current.clear();
    openedLinkRef.current = null;
    adoptedForRef.current = null;
    chatSyncRef.current.lastToken = null;
    setUsage(null);
    if (previous) clearUserSessions(previous);
    console.info('[Session] account changed; local state of the previous account cleared', {
      hadPreviousAccount: Boolean(previous),
      signedIn: Boolean(userId),
    });
  }, [userId, registry]);

  // SUBSCRIPTION_TIERS: добавлено 2026-09-17
  async function refreshUsage() {
    if (!tokenRef.current) return;
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

  // BUGFIX_PERF: добавлено 2026-09-21
  // MessageBubble is memoised, which only helps if the callbacks it receives keep one identity
  // for the whole session. The handlers below therefore read the mutable inputs through this
  // latest-values ref instead of closing over state that changes on every streamed token.
  const activeSession = sessions.find((s) => s.id === activeId) ?? null;
  const students = activeTab === 'students';
  const isAgent = activeTab === 'projects';
  const chatTab = !isAgent && !students;
  const incognitoActive = incognito && chatTab;

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

  // ---------------------------------------------------------------------------------------------
  // TURN_SCOPE: добавлено 2026-09-24 (H6/H7/M17, контракты C-1/C-5/C-6) — жизненный цикл хода.
  // Функции ниже пересоздаются на каждый рендер; долгоживущие обработчики (SignalR, сторож,
  // мемоизированные колбэки пузырей) зовут их через opsRef, поэтому всегда видят свежие значения.
  // ---------------------------------------------------------------------------------------------

  /** The turn an event belongs to, by its scope id (C-1). */
  function resolveTurn(scopeId?: string): TurnContext | undefined {
    if (scopeId) return registry.byTask(scopeId) ?? registry.bySession(scopeId);
    // An older backend sends no scope id: that is only unambiguous with exactly one live turn.
    const all = registry.all();
    return all.length === 1 ? all[0] : undefined;
  }

  /** The chat an event belongs to: its turn's chat, or a chat-scoped (workspace) event's chat. */
  function resolveChatId(scopeId?: string): string | null {
    const ctx = resolveTurn(scopeId);
    if (ctx) return ctx.sessionId;
    if (!scopeId) return null;
    const lower = scopeId.toLowerCase();
    return sessionsRef.current.find((s) => s.id.toLowerCase() === lower)?.id ?? null;
  }

  function dropEvent(event: string, scopeId?: string) {
    // Not a warning: late tokens of a turn the user already stopped land here by design.
    console.debug('[signalr] dropped an event without a matching turn', { event, scopeId });
  }

  function patchTurn(ctx: TurnContext, updater: (m: ChatMessage) => ChatMessage) {
    setSessions((prev) =>
      updateMessage(prev, ctx.sessionId, ctx.messageId, (m) => (m.status === 'streaming' ? updater(m) : m)),
    );
  }

  // SMOOTH_STREAM: queue one network chunk for the given turn and make sure a frame is scheduled.
  function enqueueContentToken(ctx: TurnContext, delta: string) {
    const key = `${ctx.sessionId}:${ctx.messageId}`;
    const entry = tokenQueueRef.current.get(key);
    if (entry) entry.text += delta;
    else tokenQueueRef.current.set(key, { ctx, text: delta });
    if (tokenFrameRef.current === null) {
      tokenFrameRef.current = window.requestAnimationFrame(releaseContentTokens);
    }
  }

  /**
   * SMOOTH_STREAM: one frame's worth of text, for every turn still buffering. A small backlog is
   * released a couple of characters at a time (smooth); a large one is flushed faster so the answer
   * never lags behind the network by more than a few frames. Everything non-content (thinking,
   * steps, statuses) still goes straight through — only the answer text is smoothed.
   */
  function releaseContentTokens() {
    tokenFrameRef.current = null;
    for (const [key, entry] of tokenQueueRef.current) {
      if (entry.text.length === 0) {
        tokenQueueRef.current.delete(key);
        continue;
      }
      const backlog = entry.text.length;
      const take = backlog > TOKEN_BACKLOG_ACCELERATE
        ? Math.min(FAST_TOKEN_CHARS_PER_FRAME, Math.ceil(backlog / 4))
        : Math.min(MAX_TOKEN_CHARS_PER_FRAME, Math.max(MIN_TOKEN_CHARS_PER_FRAME, Math.ceil(backlog / 8)));
      const slice = entry.text.slice(0, take);
      entry.text = entry.text.slice(take);
      // Hide the live action badge as soon as the final answer starts streaming.
      patchTurn(entry.ctx, (m) => ({ ...m, content: m.content + slice, currentAction: null }));
      if (entry.text.length === 0) tokenQueueRef.current.delete(key);
    }
    if (tokenQueueRef.current.size > 0) {
      tokenFrameRef.current = window.requestAnimationFrame(releaseContentTokens);
    }
  }

  /** Text still waiting in the frame queue for one turn, removed from it. */
  function drainContentTokens(sessionId: string, messageId: string): string {
    const key = `${sessionId}:${messageId}`;
    const entry = tokenQueueRef.current.get(key);
    if (!entry) return '';
    tokenQueueRef.current.delete(key);
    return entry.text;
  }

  /** Finds the assistant message of a task that is no longer (or never was) tracked here. */
  function findMessageByTask(taskId: string, includeStopped: boolean): TurnTarget | null {
    const lower = taskId.toLowerCase();
    for (const s of sessionsRef.current) {
      for (let i = s.messages.length - 1; i >= 0; i -= 1) {
        const m = s.messages[i];
        if (m.role !== 'assistant' || m.taskId?.toLowerCase() !== lower) continue;
        const eligible = m.status === 'streaming' || (includeStopped && m.status === 'stopped');
        return eligible ? { sessionId: s.id, messageId: m.id, taskId: m.taskId } : null;
      }
      // Data cached before 2026-09-24: the turn id lived only on the session.
      if (s.taskId?.toLowerCase() === lower) {
        const last = s.messages[s.messages.length - 1];
        if (last?.role === 'assistant' && last.status === 'streaming' && !last.taskId && !last.awaitingTaskId) {
          return { sessionId: s.id, messageId: last.id, taskId };
        }
      }
    }
    return null;
  }

  /** Writes the final state of a turn into its own message (never "the current" one). */
  function finalizeTurn(target: TurnTarget, outcome: TurnOutcome, opts: FinalizeOptions = {}) {
    const translate = liveRef.current.t;
    // SMOOTH_STREAM: whatever the frame queue still holds belongs to this answer. Drained here —
    // outside the state updater, which must stay pure — so a stopped turn keeps every character
    // that the server already sent, instead of losing the last few frames of it.
    const buffered = drainContentTokens(target.sessionId, target.messageId);
    setSessions((prev) => {
      let changed = false;
      const next = updateMessage(prev, target.sessionId, target.messageId, (m) => {
        const eligible = m.status === 'streaming' || (opts.allowFromStopped === true && m.status === 'stopped');
        if (!eligible) return m;
        changed = true;
        const base: ChatMessage = {
          ...m,
          content: buffered ? m.content + buffered : m.content,
          steps: closeSteps(m.steps),
          awaitingTaskId: undefined,
        };
        if (outcome === 'complete') {
          // H7: the stored result is the FULL answer. The streamed text can miss tokens that went by
          // while the socket or the page was down, so it no longer wins over the result.
          const result = opts.result;
          return { ...base, status: 'complete', content: result && result.trim() ? result : m.content, error: undefined };
        }
        if (outcome === 'stopped') return { ...base, status: 'stopped' };
        return { ...base, status: 'error', error: opts.error || translate('agent.taskFailed') };
      });
      if (!changed) return prev;
      const status = outcome === 'complete' ? 'Completed' : outcome === 'stopped' ? 'Stopped' : 'Failed';
      return updateSession(next, target.sessionId, (s) => ({ ...s, status }));
    });
  }

  // COMMAND_CONFIRM: a finished task never auto-approves, and the backend drops the flag in its own
  // finally block. Only the task that finished is cleared — another chat's card stays.
  function clearTaskApprovals(taskId?: string) {
    const lower = taskId?.toLowerCase();
    setPendingAction((p) => (p && (!lower || p.taskId?.toLowerCase() === lower) ? null : p));
    setAllowAllTaskId((id) => (id && (!lower || id.toLowerCase() === lower) ? null : id));
  }

  /** A terminal event (OnCompleted / OnError / OnStopped) finalizes ONLY its own turn. */
  function finishFromServer(taskId: string | undefined, outcome: TurnOutcome, opts: FinalizeOptions) {
    const ctx = taskId ? registry.byTask(taskId) : resolveTurn(undefined);
    const effectiveTaskId = taskId ?? ctx?.taskId;
    // TASK_COMPLETION_WATCHDOG: a turn this page no longer tracks is still found by its task id, so a
    // finished run can never leave the transcript stuck in "streaming".
    const target: TurnTarget | null = ctx
      ? { sessionId: ctx.sessionId, messageId: ctx.messageId, taskId: ctx.taskId }
      : effectiveTaskId
        ? findMessageByTask(effectiveTaskId, outcome === 'complete')
        : null;

    clearTaskApprovals(effectiveTaskId);
    // The turn is over either way: its task group is not needed any more (and is not re-joined
    // after every reconnect).
    if (effectiveTaskId) void signalrService.leaveTask(effectiveTaskId).catch(() => undefined);
    if (!target) {
      dropEvent(`terminal:${outcome}`, taskId);
      return;
    }
    if (ctx) registry.end(ctx);
    // A run the user stopped that still completed on the server shows the stored answer.
    finalizeTurn(target, outcome, { ...opts, allowFromStopped: outcome === 'complete' && !ctx });
    // SUBSCRIPTION_TIERS: добавлено 2026-09-17
    if (outcome !== 'stopped') void refreshUsage();
  }

  // CHAT_OWNERSHIP: добавлено 2026-09-24 (C-2 403 / C-6) — чат принадлежит другому аккаунту.
  function markChatForbidden(chatId: string) {
    const translate = liveRef.current.t;
    const ctx = registry.bySession(chatId);
    if (ctx) registry.end(ctx);
    setSessions((prev) =>
      updateSession(prev, chatId, (s) => ({
        ...s,
        forbidden: true,
        status: isSessionStreaming(s) ? 'Failed' : s.status,
        messages: s.messages.map((m) =>
          m.status === 'streaming'
            ? { ...m, status: 'error', error: translate('sync.chatForbidden'), steps: closeSteps(m.steps), awaitingTaskId: undefined }
            : m,
        ),
      })),
    );
    if (chatId === activeIdRef.current) showToast(translate('sync.chatForbidden'));
  }

  /** Joins the turn's task group and its chat's workspace group; a refusal is final (C-6). */
  async function joinTurnGroups(target: { sessionId: string; taskId?: string }) {
    // AGENT_EVENT_GROUPS: добавлено 2026-09-22 — бэкенд вещает в ДВЕ разные группы:
    //   * task_{taskId}  — раннер и воркер (OnAgentStatus, pending_confirmation, OnCompleted…);
    //   * task_{chatId}  — сервисы bash и редактора (started/completed/failed, TerminalOutput,
    //                      BuildProblems), потому что для них этот id — ещё и ключ воркспейса.
    // DEPLOY_WINDOW_GRACEFUL_ERRORS: подписка на группы не должна ронять отправку. Задача на
    // бэкенде УЖЕ принята, а событий мы не увидим только до того, как связь вернётся — сторож
    // (resyncTurn → joinTurnGroups) дозальёт группы сам.
    const ids = [target.taskId, target.sessionId].filter((id): id is string => Boolean(id));
    for (const id of ids) {
      try {
        await signalrService.ensureGroup(id);
      } catch (e) {
        // onJoinForbidden already marked the chat; retrying would just loop.
        if (isForbiddenJoinError(e)) return;
        console.warn('[signalr] joining the turn groups failed; the watchdog will retry', { id, error: String(e) });
      }
    }
  }

  // SIGNALR_RESILIENCE: добавлено 2026-09-22
  /**
   * Сверяет ход выполнения с сервером после обрыва связи (или перезагрузки страницы). События,
   * прошедшие пока сокет лежал, потеряны навсегда, поэтому единственный источник правды — сама
   * запись задачи. Без этого UI продолжал крутить спиннер по уже завершившейся задаче.
   */
  async function resyncTurn(target: TurnTarget & { taskId: string }, tick = 0, allowFromStopped = false): Promise<void> {
    const translate = liveRef.current.t;
    try {
      const res = await getTaskStatus(target.taskId);
      const status = (res.status ?? '').toLowerCase();
      const ctx = registry.byTask(target.taskId);

      // TASK_COMPLETION_WATCHDOG: добавлено 2026-09-22
      // TASK_COMPLETION_DIAGNOSTICS: расширено 2026-09-23 — при следующем воспроизведении
      // «висит генерация» по этой строке видно ЦЕЛИКОМ решение сторожа.
      console.info('[Watchdog] poll', { taskId: target.taskId, tick, status, resultChars: res.result?.length ?? 0 });

      if (status === 'running' || status === 'pending') {
        // Задача жива: гарантируем членство в её группах, чтобы поток событий возобновился.
        if (ctx) void joinTurnGroups(ctx);
        return;
      }

      // TASK_COMPLETION_WATCHDOG: терминальный статус на сервере означает, что мы больше не в стриме.
      if (ctx) registry.end(ctx);
      const session = sessionsRef.current.find((s) => s.id === target.sessionId);
      const outcome: TurnOutcome = status === 'completed' ? 'complete' : status === 'cancelled' ? 'stopped' : 'error';
      finalizeTurn(target, outcome, {
        // An incognito task record keeps only a placeholder instead of the answer.
        result: session?.incognito ? undefined : (res.result ?? undefined),
        error: res.result || translate('agent.taskFailed'),
        allowFromStopped,
      });
      clearTaskApprovals(target.taskId);
      void signalrService.leaveTask(target.taskId).catch(() => undefined);

      console.info('[Watchdog] finalizing turn from server state', {
        sessionId: target.sessionId,
        taskId: target.taskId,
        tick,
        status,
        wasStreaming: Boolean(ctx),
      });
    } catch (err) {
      if (httpStatus(err) === 404) {
        // The task record does not exist for this account: polling it again cannot help.
        const ctx = registry.byTask(target.taskId);
        if (ctx) registry.end(ctx);
        finalizeTurn(target, 'error', { error: translate('agent.taskFailed'), allowFromStopped });
        console.warn('[Watchdog] task not found; turn closed', { taskId: target.taskId, tick });
        return;
      }
      // Недоступная задача не фатальна — транскрипт остаётся как есть. Но молчать нельзя:
      // именно проглатывание ошибки опроса делало баг невидимым (502 от прокси = вечный спиннер).
      console.warn('[Watchdog] could not read task state', { taskId: target.taskId, tick, error: String(err) });
    }
  }

  // H7: ход, начатый до перезагрузки (или в другой вкладке), «усыновляется»: получает контекст,
  // подписку на обе группы, опрос сторожем и рабочую кнопку «Стоп».
  function adoptTurn(target: TurnTarget & { taskId: string }) {
    if (registry.bySession(target.sessionId)) return;
    const ctx: TurnContext = { ...target, adopted: true, startedAt: Date.now() };
    registry.begin(ctx);
    setSessions((prev) => updateSession(prev, target.sessionId, (s) => (s.status === 'Running' ? s : { ...s, status: 'Running' })));
    console.info('[Resync] adopting a turn left running before the reload', { sessionId: target.sessionId, taskId: target.taskId });
    void (async () => {
      // Join FIRST, then read the record: a turn that finished before the join is finalized from
      // the server's result instead of spinning forever; one that finishes after it sends OnCompleted.
      await joinTurnGroups(ctx);
      if (!registry.isActive(ctx)) return;
      await resyncTurn(target, 0);
    })();
  }

  // STOP_CONFIRM: добавлено 2026-09-24 (M17, C-5) — ответ хаба больше не игнорируется.
  async function stopOnServer(target: TurnTarget & { taskId: string }) {
    const translate = liveRef.current.t;
    try {
      const result = await signalrService.stopGeneration(target.taskId);
      console.info('[Stop] server answered', { taskId: target.taskId, result });
      // `stopping`: OnStopped follows. `cancelled` (the turn never ran) / `not_found` (it had already
      // finished): nothing more will come — the task record is the final truth.
      if (result !== 'stopping') await resyncTurn(target, 0, true);
    } catch (err) {
      console.warn('[Stop] StopGeneration failed', { taskId: target.taskId, error: String(err) });
      showToast(translate('sync.stopFailed'));
      // The turn most likely keeps running: show it as running again so Stop can be pressed again.
      reattachTurn(target);
    }
  }

  function reattachTurn(target: TurnTarget & { taskId: string }) {
    if (registry.bySession(target.sessionId)) return;
    const session = sessionsRef.current.find((s) => s.id === target.sessionId);
    const message = session?.messages.find((m) => m.id === target.messageId);
    if (!message || message.status !== 'stopped') return;
    setSessions((prev) =>
      updateSession(prev, target.sessionId, (s) => ({
        ...s,
        status: 'Running',
        messages: s.messages.map((m) => (m.id === target.messageId ? { ...m, status: 'streaming' } : m)),
      })),
    );
    const ctx: TurnContext = { ...target, adopted: true, startedAt: Date.now() };
    registry.begin(ctx);
    void joinTurnGroups(ctx);
  }

  const opsRef = useRef({
    resyncTurn,
    adoptTurn,
    finalizeTurn,
    joinTurnGroups,
    stopOnServer,
    markChatForbidden,
    clearTaskApprovals,
  });
  opsRef.current = {
    resyncTurn,
    adoptTurn,
    finalizeTurn,
    joinTurnGroups,
    stopOnServer,
    markChatForbidden,
    clearTaskApprovals,
  };

  // SMOOTH_STREAM: tokens are buffered here per turn and released on animation frames (see
  // releaseContentTokens). The queue lives in a ref because it is written from the SignalR handler
  // and read by the frame callback — neither of which should re-render on its own.
  const tokenQueueRef = useRef(new Map<string, { ctx: TurnContext; text: string }>());
  const tokenFrameRef = useRef<number | null>(null);

  // MOBILE_DRAWER: the drawer transform is written directly to these nodes during a swipe.
  const sidebarPanelRef = useRef<HTMLElement | null>(null);
  const sidebarBackdropRef = useRef<HTMLDivElement | null>(null);

  // STREAM_SCOPE: добавлено 2026-09-24 (H6, C-1) — каждое событие маршрутизируется по scopeId в
  // СВОЙ ход; то, что сопоставить нельзя, отбрасывается, а не пишется в «текущее» сообщение.
  const eventsRef = useRef<SignalrCallbacks>({});
  eventsRef.current = {
    onContentToken: (delta, scopeId) => {
      const ctx = resolveTurn(scopeId);
      if (!ctx) return dropEvent('OnContentToken', scopeId);
      enqueueContentToken(ctx, delta);
    },
    onThinkingToken: (delta, scopeId) => {
      const ctx = resolveTurn(scopeId);
      if (!ctx) return dropEvent('OnThinkingToken', scopeId);
      patchTurn(ctx, (m) => ({ ...m, thinking: (m.thinking ?? '') + delta }));
    },
    onLog: (message, scopeId) => {
      const ctx = resolveTurn(scopeId);
      if (!ctx) return dropEvent('OnLog', scopeId);
      patchTurn(ctx, (m) => ({ ...m, logs: [...(m.logs ?? []), message] }));
    },
    onScreenshot: (base64, scopeId) => {
      const ctx = resolveTurn(scopeId);
      if (!ctx) return dropEvent('OnScreenshot', scopeId);
      patchTurn(ctx, (m) => ({ ...m, screenshots: [...(m.screenshots ?? []), base64] }));
    },
    onAgentStatus: (payload, scopeId) => {
      const ctx = resolveTurn(scopeId);
      if (!ctx) return dropEvent('OnAgentStatus', scopeId);
      patchTurn(ctx, (m) => {
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
      });
    },
    onFileCreated: (payload, scopeId) => {
      // The workspace panel shows the OPEN chat: a file created by another chat's agent must not
      // open a tab there.
      const chatId = resolveChatId(scopeId);
      if (!chatId || chatId !== activeIdRef.current) return dropEvent('OnFileCreated', scopeId);
      setFileCreatedEvent(payload);
    },
    onToolAction: (event, scopeId) => {
      // Workspace events (bash, editor) are scoped by the chat id and belong to that chat's turn.
      const ctx = resolveTurn(scopeId);
      if (ctx) {
        patchTurn(ctx, (m) => ({ ...m, toolActions: [...(m.toolActions ?? []), event] }));
      }
      const chatId = ctx?.sessionId ?? resolveChatId(scopeId);
      if (!chatId) return dropEvent('ToolAction', scopeId);
      // Refresh the file tree when the editor creates/edits a file, so the
      // new/modified file appears in the IDE without a manual refresh.
      if (
        chatId === activeIdRef.current &&
        event.toolName === 'str_replace_editor' &&
        (event.command === 'create' || event.command === 'str_replace' || event.command === 'insert') &&
        event.status === 'completed'
      ) {
        setFileRefreshToken((n) => n + 1);
        setAgentFileChange({ path: event.path });
      }
    },
    onTodoUpdate: (payload, scopeId) => {
      const ctx = resolveTurn(scopeId);
      if (!ctx) return dropEvent('TodoUpdate', scopeId);
      patchTurn(ctx, (m) => ({ ...m, todos: payload.todos }));
    },
    onCompleted: (payload, scopeId) => {
      finishFromServer(payload?.taskId ?? scopeId, 'complete', { result: payload?.result });
      // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
      // liveRespondRef.current?.resolve(payload.result);
    },
    onError: (err, scopeId) => {
      finishFromServer(scopeId, 'error', { error: err });
    },
    onStopped: (taskId, scopeId) => {
      finishFromServer(taskId || scopeId, 'stopped', {});
    },
    onRunProjectError: (payload) => {
      showToast(liveRef.current.t('toast.runError', { message: payload.message }));
    },
    onSearchStatus: (payload, scopeId) => {
      const ctx = resolveTurn(scopeId);
      if (!ctx) return dropEvent('SearchStatus', scopeId);
      // L13: the label is built at event time in the CURRENT language.
      const translate = liveRef.current.t;
      const action = payload.status === 'searching'
        ? { stage: 'searching', label: payload.query ? translate('agent.searching', { query: payload.query }) : translate('agent.searchingShort') }
        : null;
      patchTurn(ctx, (m) => ({ ...m, currentAction: action }));
    },
    onProblems: (payload, scopeId) => {
      const ctx = resolveTurn(scopeId);
      if (!ctx) return dropEvent('BuildProblems', scopeId);
      patchTurn(ctx, (m) => ({ ...m, problems: payload.problems ?? [] }));
    },
    // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
    onPendingActionCreated: (payload, scopeId) => {
      if (!resolveTurn(scopeId) && !registry.byTask(payload.taskId)) return dropEvent('OnPendingActionCreated', scopeId);
      setPendingAction(payload);
    },
    // SIGNALR_RESILIENCE: добавлено 2026-09-22 — после успешного переподключения события,
    // которые не дошли, уже не вернуть, поэтому состояние КАЖДОГО идущего хода перечитываем.
    onReconnected: () => {
      for (const ctx of registry.all()) {
        if (ctx.taskId) void resyncTurn({ sessionId: ctx.sessionId, messageId: ctx.messageId, taskId: ctx.taskId });
      }
    },
    // CHAT_OWNERSHIP (C-6): вход в группу запрещён навсегда — чат не наш.
    onJoinForbidden: (id) => {
      const ctx = registry.byTask(id) ?? registry.bySession(id);
      const chatId = ctx?.sessionId ?? resolveChatId(id) ?? findMessageByTask(id, true)?.sessionId ?? null;
      if (chatId) markChatForbidden(chatId);
    },
  };

  // Соединение живёт, пока не сменится пользователь. Выход закрывает его и забывает токен и группы.
  useEffect(() => {
    const currentToken = tokenRef.current;
    if (!userId || !currentToken) {
      void signalrService.disconnect();
      return;
    }

    let disposed = false;
    const ev = (): SignalrCallbacks => (disposed ? {} : eventsRef.current);

    void signalrService.connect(currentToken, {
      onContentToken: (delta, scopeId) => ev().onContentToken?.(delta, scopeId),
      onThinkingToken: (delta, scopeId) => ev().onThinkingToken?.(delta, scopeId),
      onLog: (message, scopeId) => ev().onLog?.(message, scopeId),
      onScreenshot: (base64, scopeId) => ev().onScreenshot?.(base64, scopeId),
      onAgentStatus: (payload, scopeId) => ev().onAgentStatus?.(payload, scopeId),
      onFileCreated: (payload, scopeId) => ev().onFileCreated?.(payload, scopeId),
      onToolAction: (event, scopeId) => ev().onToolAction?.(event, scopeId),
      onTodoUpdate: (payload, scopeId) => ev().onTodoUpdate?.(payload, scopeId),
      onCompleted: (payload, scopeId) => ev().onCompleted?.(payload, scopeId),
      onError: (error, scopeId) => ev().onError?.(error, scopeId),
      onStopped: (taskId, scopeId) => ev().onStopped?.(taskId, scopeId),
      onRunProjectError: (payload) => ev().onRunProjectError?.(payload),
      onSearchStatus: (payload, scopeId) => ev().onSearchStatus?.(payload, scopeId),
      onProblems: (payload, scopeId) => ev().onProblems?.(payload, scopeId),
      onPendingActionCreated: (payload, scopeId) => ev().onPendingActionCreated?.(payload, scopeId),
      onReconnected: () => ev().onReconnected?.(),
      onJoinForbidden: (id) => ev().onJoinForbidden?.(id),
    });

    return () => {
      disposed = true;
      void signalrService.disconnect();
    };
  }, [userId]);

  // TASK_COMPLETION_WATCHDOG: добавлено 2026-09-22
  // Гарантия, что генерация завершается САМА. Живое событие OnCompleted приходит по SignalR и в
  // редких случаях может быть потеряно (короткий прогон завершился до подписки на группу,
  // переподключение, обрыв). Состояние сверяется с записью задачи, пока идёт стрим, и сообщение
  // закрывается автоматически. TURN_SCOPE: теперь опрашивается КАЖДЫЙ идущий ход, а не один.
  useEffect(() => {
    const id = window.setInterval(() => {
      watchdogTickRef.current += 1;
      const tick = watchdogTickRef.current;
      for (const ctx of registry.all()) {
        if (!ctx.taskId) {
          // POST /run ещё не вернулся. Долго без taskId — это видно в консоли, а не молча.
          if (Date.now() - ctx.startedAt > 30_000) {
            console.warn('[Watchdog] streaming turn has no taskId yet — completion cannot be verified', {
              tick,
              sessionId: ctx.sessionId,
              messageId: ctx.messageId,
            });
          }
          continue;
        }
        void opsRef.current.resyncTurn({ sessionId: ctx.sessionId, messageId: ctx.messageId, taskId: ctx.taskId }, tick);
      }
    }, 5000);
    return () => window.clearInterval(id);
  }, [registry]);

  // SIGNALR_RESILIENCE: добавлено 2026-09-22 — после перезагрузки в localStorage может остаться ход,
  // который был в процессе, когда вкладка закрылась: статус «streaming» без единого события о
  // завершении. H7 (2026-09-24): такой ход не просто сверяется, а УСЫНОВЛЯЕТСЯ.
  useEffect(() => {
    if (!token || !storeUser || storeUser !== userId || adoptedForRef.current === storeUser) return;
    adoptedForRef.current = storeUser;

    for (const session of sessionsRef.current) {
      if (session.incognito || !isSessionStreaming(session)) continue;
      // Ход, который ведёт текущая вкладка, уже отслеживается живьём — не мешаем ему.
      if (registry.bySession(session.id)) continue;
      const last = session.messages[session.messages.length - 1];
      const taskId = last.taskId ?? (last.awaitingTaskId ? undefined : session.taskId);
      if (!taskId) {
        // POST /run так и не ответил (вкладку закрыли раньше) — сервер ход не подтверждал.
        console.info('[Resync] a turn was never confirmed by the server; marking it stopped', { sessionId: session.id });
        opsRef.current.finalizeTurn({ sessionId: session.id, messageId: last.id }, 'stopped');
        continue;
      }
      opsRef.current.adoptTurn({ sessionId: session.id, messageId: last.id, taskId });
    }
  }, [token, storeUser, userId, registry]);

  const syncChats = useCallback(async () => {
    const userAtStart = storeUserRef.current;
    if (!tokenRef.current || !userAtStart) return;

    const state = chatSyncRef.current;
    // Один запрос за раз: параллельные тики (focus + visibilitychange) не должны дублироваться.
    if (state.inFlight) return;
    state.inFlight = true;
    state.lastStartedAt = Date.now();

    const isBusy = (s: ChatSession) => Boolean(registryRef.current?.bySession(s.id)) || isSessionStreaming(s);

    try {
      const syncStartedAt = Date.now();
      const { chats, allChatIds } = await getChats(CHAT_LIST_LIMIT);
      // Аккаунт сменился, пока шёл запрос: этот ответ относится к другому пользователю.
      if (storeUserRef.current !== userAtStart) return;
      const translate = liveRef.current.t;

      const lower = (id: string) => id.toLowerCase();
      // CHAT_LIST_COMPLETE (H8): полный набор id — единственное, по чему можно удалять локальные чаты.
      const allIds = allChatIds ? new Set(allChatIds.map(lower)) : null;
      const summaries = new Map(chats.map((c) => [lower(c.id), c]));
      const serverIds = allIds ?? new Set(summaries.keys());
      // CHAT_RENAME: серверные имена — для актуализации тех чатов, что уже есть локально.
      const serverTitles = new Map(chats.map((c) => [lower(c.id), c.title ?? null]));
      // CHAT_PIN: серверное закрепление — то же самое, но для порядка в сайдбаре.
      const serverPins = new Map(chats.map((c) => [lower(c.id), c.isPinned]));

      const snapshot = sessionsRef.current;
      const known = new Set(snapshot.map((s) => lower(s.id)));

      // SESSION_ISOLATION (H10): чаты из старого общего кеша — только подтверждённые сервером как свои.
      const migrated = allIds ? takeLegacySessions(allIds).filter((s) => !known.has(lower(s.id))) : [];
      const migratedIds = new Set(migrated.map((s) => lower(s.id)));

      // Чаты, которых на этом устройстве нет: свежие — сразу с перепиской, остальные — по открытию.
      const missing = chats.filter((c) => !known.has(lower(c.id)) && !migratedIds.has(lower(c.id)));
      const eager = new Set(missing.slice(0, EAGER_TRANSCRIPTS).map((c) => c.id));
      const restored = await mapLimit(missing, TRANSCRIPT_CONCURRENCY, async (chat) => {
        // A missing transcript must not drop the chat itself from the list.
        const transcript = eager.has(chat.id)
          ? await getChatTranscript(chat.id).catch((e: unknown) => {
              console.warn('[ChatSync] transcript unavailable', { chatId: chat.id, error: String(e) });
              return null;
            })
          : null;
        return sessionFromServer(chat, transcript, defaultTitle(kindFromServer(chat.kind), translate));
      });

      // TRANSCRIPT_REFRESH (M25): чат, продолженный на другом устройстве, — у сервера новее
      // lastActivityAt / другое число сообщений. Изменение, которое сделали мы сами (unsyncedTurns),
      // и чат, где сейчас идёт генерация, не трогаем.
      interface RefreshPlan { summary: ChatSummary; unsyncedAtStart: number; refetch: boolean }
      const plans = new Map<string, RefreshPlan>();
      for (const s of [...snapshot, ...migrated]) {
        if (s.incognito || !s.remote || isBusy(s)) continue;
        const summary = summaries.get(lower(s.id));
        if (!summary) continue;
        const baselineKnown = s.serverActivityAt !== undefined;
        const changed = !baselineKnown
          || s.serverActivityAt !== summary.lastActivityAt
          || s.serverMessageCount !== summary.messageCount;
        if (!changed) continue;
        const unsynced = s.unsyncedTurns ?? 0;
        const fromLegacy = migratedIds.has(lower(s.id));
        plans.set(s.id, {
          summary,
          unsyncedAtStart: unsynced,
          // A chat seen for the first time since this version only records the baseline.
          refetch: !s.needsTranscript && unsynced === 0 && (baselineKnown || fromLegacy),
        });
      }
      const fetched = new Map(
        await mapLimit([...plans].filter(([, p]) => p.refetch), TRANSCRIPT_CONCURRENCY, async ([id]) => {
          const transcript = await getChatTranscript(id).catch((e: unknown) => {
            console.warn('[ChatSync] could not refresh a transcript', { chatId: id, error: String(e) });
            return null;
          });
          return [id, transcript] as const;
        }),
      );
      if (storeUserRef.current !== userAtStart) return;

      let added = 0;
      let pruned = 0;
      let retitled = 0;
      let repinned = 0;
      let refreshed = 0;
      setSessions((prev) => {
        const existing = new Set(prev.map((s) => lower(s.id)));
        let changedAny = false;

        // CHAT_RENAME: имя тоже берём с сервера — так переименование, сделанное на телефоне,
        // доезжает до этого устройства. CHAT_PIN: и закрепление — оно задаёт порядок.
        // TRANSCRIPT_REFRESH (M25): и переписка чата, продолженного на другом устройстве.
        const refresh = (s: ChatSession): ChatSession => {
          let next = s;
          const key = lower(s.id);
          const serverTitle = serverTitles.get(key);
          // Наш rename новее этого ответа — он и побеждает.
          const recentRename = recentRenamesRef.current.get(s.id);
          const titleWins = Boolean(serverTitle) && serverTitle !== s.title
            && !(recentRename && recentRename.at > syncStartedAt);

          const serverPin = serverPins.get(key);
          const recentPin = recentPinsRef.current.get(s.id);
          // Пока пин не подтверждён сервером, локальное значение важнее серверного.
          const pinWins = serverPin !== undefined && serverPin !== (s.isPinned ?? false)
            && pendingPinsRef.current.get(s.id) === undefined
            && !(recentPin && recentPin.at > syncStartedAt);

          if (titleWins || pinWins) {
            if (titleWins) retitled += 1;
            if (pinWins) repinned += 1;
            next = {
              ...next,
              ...(titleWins ? { title: serverTitle as string } : {}),
              ...(pinWins ? { isPinned: serverPin } : {}),
            };
          }

          const plan = plans.get(s.id);
          const transcript = plan ? fetched.get(s.id) : undefined;
          // A turn started while we were fetching, or the transcript could not be read: leave the
          // baseline as it is, so the next sync looks at this chat again.
          if (plan && !isBusy(s) && !(plan.refetch && !transcript)) {
            if (plan.refetch && transcript) {
              const merged = mergeTranscript(s.messages, messagesFromTranscript(transcript));
              if (merged) {
                refreshed += 1;
                next = { ...next, messages: merged };
              }
              // CHAT_MODEL_SYNC (M18): чат, продолженный в другом режиме, открывается в нём.
              if (plan.summary.model) next = { ...next, model: modelFromServer(next.kind ?? 'chat', plan.summary.model) };
            }
            next = {
              ...next,
              serverActivityAt: plan.summary.lastActivityAt,
              serverMessageCount: plan.summary.messageCount,
              unsyncedTurns: Math.max(0, (s.unsyncedTurns ?? 0) - plan.unsyncedAtStart),
            };
          }

          if (next !== s) changedAny = true;
          return next;
        };

        // CHAT_DELETE: для чатов, которые были на сервере, источник правды — сервер.
        // CHAT_LIST_COMPLETE (H8): удаляем локальный серверный чат ТОЛЬКО если его id нет в полном
        // наборе allChatIds. Никогда: без полного набора (старый бэкенд), закреплённые, открытый чат,
        // чат с идущей генерацией (его история ещё не записана), локальные черновики и инкогнито.
        const kept = prev.filter((s) => {
          if (s.incognito || !s.remote || !allIds) return true;
          if (allIds.has(lower(s.id))) return true;
          if (s.isPinned || s.id === activeIdRef.current || isBusy(s)) return true;
          return false;
        }).map(refresh);
        const fresh = [...migrated.map(refresh), ...restored].filter((s) => !existing.has(lower(s.id)));

        added = fresh.length;
        // map длины не меняет, поэтому длина kept и есть число оставленных чатов.
        pruned = prev.length - kept.length;
        if (fresh.length === 0 && pruned === 0 && !changedAny) return prev;
        return [...kept, ...fresh];
      });

      // Записи о переименованиях нужны только против ответов, ушедших до них.
      for (const [chatId, entry] of recentRenamesRef.current) {
        if (entry.at <= syncStartedAt) recentRenamesRef.current.delete(chatId);
      }
      // То же для закреплений.
      for (const [chatId, entry] of recentPinsRef.current) {
        if (entry.at <= syncStartedAt) recentPinsRef.current.delete(chatId);
      }

      // CHAT_PIN_PENDING: ход уже сохранён, значит у чата появились строки — досылаем закрепления,
      // которые в момент клика сервер ещё не знал (см. pendingPinsRef выше).
      let replayedPins = 0;
      for (const [chatId, desired] of pendingPinsRef.current) {
        if (!serverIds.has(lower(chatId))) continue;
        try {
          await setChatPinned(chatId, desired);
          pendingPinsRef.current.delete(chatId);
          recentPinsRef.current.set(chatId, { isPinned: desired, at: Date.now() });
          replayedPins += 1;
        } catch (e) {
          // Оставляем в pending: следующая сверка попробует снова.
          console.warn('[ChatPin] could not re-apply a pending pin', { chatId, error: String(e) });
        }
      }

      console.info('[ChatSync] chat list synced', {
        fetched: chats.length,
        total: allChatIds?.length ?? null,
        added,
        migrated: migrated.length,
        pruned,
        retitled,
        repinned,
        refreshed,
        replayedPins,
      });
    } catch (e) {
      // Offline or a failed request must not break the app: the local list stays as it is.
      console.warn('[ChatSync] could not load the chat list', e);
    } finally {
      chatSyncRef.current.inFlight = false;
    }
  }, []);

  useEffect(() => {
    if (!token || chatSyncRef.current.lastToken === token) return;
    chatSyncRef.current.lastToken = token;
    void syncChats();
  }, [token, userId, syncChats]);

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

  // LOCAL_STORAGE_BUDGET: добавлено 2026-09-24 (M22) — переписка чата, которой нет в кеше этого
  // устройства (вытеснена ради места или ещё не загружалась), грузится с сервера при открытии.
  function loadTranscript(chatId: string) {
    if (transcriptLoadingRef.current.has(chatId)) return;
    transcriptLoadingRef.current.add(chatId);
    setTranscriptLoad({ id: chatId, failed: false });
    getChatTranscript(chatId)
      .then((transcript) => {
        setSessions((prev) =>
          updateSession(prev, chatId, (s) =>
            !s.needsTranscript || isSessionStreaming(s)
              ? s
              : { ...s, messages: messagesFromTranscript(transcript), needsTranscript: undefined },
          ),
        );
        setTranscriptLoad((cur) => (cur?.id === chatId ? null : cur));
      })
      .catch((e: unknown) => {
        const status = httpStatus(e);
        if (status === 403) {
          markChatForbidden(chatId);
          setTranscriptLoad((cur) => (cur?.id === chatId ? null : cur));
          return;
        }
        if (status === 404) {
          // Nothing is stored for this chat (any more): show it empty rather than loading forever.
          setSessions((prev) => updateSession(prev, chatId, (s) => ({ ...s, needsTranscript: undefined })));
          setTranscriptLoad((cur) => (cur?.id === chatId ? null : cur));
          return;
        }
        console.warn('[ChatSync] could not load the transcript of the open chat', { chatId, error: String(e) });
        setTranscriptLoad({ id: chatId, failed: true });
      })
      .finally(() => {
        transcriptLoadingRef.current.delete(chatId);
      });
  }

  useEffect(() => {
    if (!token || !activeSession?.needsTranscript) return;
    if (transcriptLoad?.id === activeSession.id && transcriptLoad.failed) return; // waits for "Retry"
    loadTranscript(activeSession.id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [token, activeSession?.id, activeSession?.needsTranscript]);

  // FILE_DROP: добавлено 2026-09-24 (L11) — файл, брошенный мимо колонки чата, не уводит вкладку.
  usePreventWindowFileDrop();
  const dropZone = useFileDropZone((files) => {
    dropNonceRef.current += 1;
    setDroppedFiles({ files, nonce: dropNonceRef.current });
  }, Boolean(token));

  const mode: 'chat' | 'code' = activeTab === 'projects' ? 'code' : 'chat';
  // TURN_SCOPE (H6/L8): «идёт генерация» — это состояние ОТКРЫТОГО чата, а не глобальный флаг.
  const activeStreaming = isSessionStreaming(activeSession);
  const agentRunning = activeStreaming || activeSession?.status === 'Running';
  const activeLoading = Boolean(activeSession?.needsTranscript) && !(transcriptLoad?.id === activeSession?.id && transcriptLoad?.failed);
  const activeLoadFailed = Boolean(activeSession?.needsTranscript) && transcriptLoad?.id === activeSession?.id && Boolean(transcriptLoad?.failed);

  // INCOGNITO_CHAT: добавлено 2026-09-20
  // The switch is offered only for a still-empty plain chat: once the first message is sent the
  // mode is locked in (the header keeps a passive badge so the user cannot forget it).
  const chatEmpty = (activeSession?.messages.length ?? 0) === 0 && !activeSession?.needsTranscript;
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

  // Latest agent progress for the IDE bottom panel (the task checklist).
  const lastAssistant = [...(activeSession?.messages ?? [])].reverse().find((m) => m.role === 'assistant') ?? null;
  const latestTodos = lastAssistant?.todos ?? [];

  // STATUS_BAR: изменено 2026-09-24 (L8) — статус-бар выводится из состояния открытого чата.
  // Раньше в него писалась подпись фазы агента («Анализирую…»), которую статус-бар не узнавал и
  // показывал как «Готово», а после ошибки отправки там навсегда оставалось «Работает…».
  const agentStatus: AgentStatus = agentRunning
    ? 'Working…'
    : activeSession?.status === 'Completed'
      ? 'Completed'
      : activeSession?.status === 'Failed'
        ? 'Failed'
        : activeSession?.status === 'Stopped'
          ? 'Stopped'
          : 'Ready';
  const agentActivity = agentRunning ? (lastAssistant?.currentAction?.label ?? null) : null;

  // Reset the editor cursor info when switching sessions.
  useEffect(() => {
    setCursorInfo(null);
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

  // WORKSPACE_DIRTY: добавлено 2026-09-24 — несохранённые вкладки редактора подтверждаются ДО того,
  // как открытый чат сменится: после смены WorkspacePanel уже перезагрузит вкладки нового чата.
  const workspaceShown = isAgent && !isMobile && !ideCollapsed;
  function guardLeave(action: () => void) {
    if (workspaceShown && workspaceDirty) {
      setPendingLeave(() => action);
      return;
    }
    action();
  }
  const guardLeaveRef = useRef(guardLeave);
  guardLeaveRef.current = guardLeave;

  // NEW_CHAT_LOGO: добавлено 2026-09-20
  /** Starts a fresh conversation in the current tab (used by the mark under a reply). */
  const handleNewChat = useCallback(() => {
    guardLeaveRef.current(() => handleTabChange(liveRef.current.activeTab));
  }, [handleTabChange]);

  function handleModelChange(next: ConexyModel) {
    setModel(next);
    // The reasoning/thinking toggle is only meaningful for ConexyV1-pro. Drop its state
    // when switching to a model that ignores it, so a stale value can't leak into a request.
    if (next !== 'ConexyV1-pro') {
      setThinking(false);
    }
  }

  // COWORK_MODE / CHAT_MODEL_SYNC: изменено 2026-09-24 (M18) — открытый чат восстанавливает свою
  // модель: агентский — Coder или Cowork, обычный — flash или pro, «Ученики» — всегда pro.
  // Иначе следующее сообщение молча ушло бы в ту модель, что последней показывал переключатель.
  function applySessionModel(s: ChatSession) {
    const kind = s.kind ?? 'chat';
    if (kind === 'projects') {
      setModel(isAgentModel(s.model) ? s.model : 'conexy-coder');
    } else if (kind === 'students') {
      setModel('ConexyV1-pro');
    } else {
      const next = s.model === 'ConexyV1-pro' ? 'ConexyV1-pro' : 'ConexyV1-flash';
      setModel(next);
      if (next !== 'ConexyV1-pro') setThinking(false);
    }
  }

  function openSession(id: string) {
    setActiveId(id);
    const selected = sessionsRef.current.find((s) => s.id === id);
    if (selected) applySessionModel(selected);
    // INCOGNITO_CHAT: opening another chat always returns to normal mode.
    setIncognito(false);
    if (isMobile) setSidebarOpen(false);
  }

  function handleNewSession(_kind: ChatSessionKind): string {
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

  // CHAT_PIN: добавлено 2026-09-23 — закрепление живёт на сервере, чтобы порядок сайдбара был
  // одинаковым на всех устройствах (раньше флаг оставался только в этом браузере).
  async function handlePinSession(id: string) {
    const session = sessionsRef.current.find((s) => s.id === id);
    const next = !(session?.isPinned ?? false);
    // Черновик и инкогнито-чат на сервере не существуют — закрепление останется локальным.
    const shouldAskServer = Boolean(session) && !session!.incognito && session!.remote && GUID_LIKE.test(id);

    // Оптимистично: порядок должен перестроиться сразу, а не после ответа сервера.
    setSessions((prev) => prev.map((s) => (s.id === id ? { ...s, isPinned: next } : s)));

    if (!shouldAskServer) return;

    try {
      await setChatPinned(id, next);
      // Отмечаем время переключения, чтобы синхронизация, ушедшая до него, не вернула старый флаг.
      recentPinsRef.current.set(id, { isPinned: next, at: Date.now() });
      pendingPinsRef.current.delete(id);
    } catch (e) {
      const status = httpStatus(e);
      // 404 = у чата ещё нет ни одной строки истории (ход не дописан), поэтому пин некуда сохранить.
      // Не откатываем и не теряем его: запоминаем как неподтверждённый и досылаем в syncChats, как
      // только сервер начнёт отдавать этот чат. Любая другая ошибка — откат, иначе состояние
      // разойдётся с сервером и следующий sync молча вернёт старый порядок.
      if (status === 404) {
        console.warn('[ChatPin] the chat is not stored yet; pin kept locally', { id });
        pendingPinsRef.current.set(id, next);
        return;
      }

      console.warn('[ChatPin] could not pin the chat on the server', { id, status, error: String(e) });
      showToast(t('chat.pinFailed'));
      setSessions((prev) => prev.map((s) => (s.id === id ? { ...s, isPinned: !next } : s)));
    }
  }

  // CHAT_RENAME: добавлено 2026-09-23 — имя сохраняется на сервере, чтобы приехать на другое устройство.
  async function handleRenameSession(id: string, title: string) {
    const trimmed = title.trim();
    if (!trimmed) return;

    const session = sessionsRef.current.find((s) => s.id === id);
    // Черновик и инкогнито-чат на сервере не существуют — переименование останется локальным.
    const shouldAskServer = Boolean(session) && !session!.incognito && session!.remote && GUID_LIKE.test(id);

    if (shouldAskServer) {
      try {
        await renameChat(id, trimmed);
      } catch (e) {
        const status = httpStatus(e);
        // 404 = сервер такого чата у этого пользователя не знает: локальное имя всё равно корректно.
        // Любая другая ошибка — не применяем, иначе имя разойдётся с сервером и будет перезаписано
        // ближайшей синхронизацией.
        if (status !== 404) {
          console.warn('[ChatRename] could not rename the chat on the server', { id, status, error: String(e) });
          showToast(t('chat.renameFailed'));
          return;
        }
      }
    }

    setSessions((prev) => prev.map((s) => (s.id === id ? { ...s, title: trimmed } : s)));
    // Отмечаем время переименования, чтобы синхронизация, ушедшая до него, не вернула старое имя.
    recentRenamesRef.current.set(id, { title: trimmed, at: Date.now() });
  }

  // CHAT_DELETE: добавлено 2026-09-23
  // Удаление теперь физическое: сначала сервер (история + рабочая область), и только потом
  // локальный список. Раньше чат исчезал только в этом браузере, а следующий sync возвращал его из
  // базы обратно.
  async function handleDeleteSession(id: string) {
    const session = sessionsRef.current.find((s) => s.id === id);
    // A chat that never reached the server (draft, incognito) has nothing to delete there.
    const shouldAskServer = Boolean(session) && !session!.incognito && GUID_LIKE.test(id);

    // TURN_SCOPE: идущий в этом чате ход сначала останавливается — иначе воркер в finally записал
    // бы его историю обратно, и удалённый чат «воскрес» бы (M8).
    const ctx = registry.bySession(id);
    if (ctx?.taskId) {
      await signalrService.stopGeneration(ctx.taskId).catch((e: unknown) => {
        console.warn('[ChatDelete] could not stop the running turn first', { id, error: String(e) });
      });
    } else if (ctx) {
      ctx.stopRequested = true;
    }

    if (shouldAskServer) {
      try {
        await deleteChat(id);
      } catch (e) {
        const status = httpStatus(e);
        // 404 means the server has no such chat for this user: it is safe (and correct) to drop the
        // local copy anyway. Any other failure must NOT pretend the delete happened.
        if (status !== 404) {
          console.warn('[ChatDelete] could not delete the chat on the server', { id, status, error: String(e) });
          showToast(t('chat.deleteFailed'));
          return;
        }
      }
    }

    // Clear every reference to the deleted chat so it can never be re-opened: the session list,
    // the active id, and its turn (whose late SignalR deltas are dropped from now on).
    registry.end(registry.bySession(id));
    setSessions((prev) => prev.filter((s) => s.id !== id));
    if (activeIdRef.current === id) {
      setActiveId(uid());
      // INCOGNITO_CHAT: the deleted chat took its mode with it.
      setIncognito(false);
    }
  }

  // CHAT_SHARE: на телефоне — системная шторка через Web Share API, на десктопе — ссылка в
  // буфер обмена. Раньше кнопка просто копировала весь текст диалога с англоязычным тостом.
  async function handleShareSession(session: ChatSession) {
    // Черновик и инкогнито-чат на сервере не существуют — ссылке некуда вести.
    if (session.incognito || !session.remote || !GUID_LIKE.test(session.id)) {
      showToast(t('sidebar.shareUnavailable'));
      return;
    }

    const url = chatLink(session.id);
    const title = session.title || t('chat.shareTitleFallback');

    // navigator.share есть и в части десктопных браузеров — тогда системная шторка предпочтительнее.
    if (navigator.share) {
      try {
        await navigator.share({ title, text: title, url });
      } catch (e) {
        // AbortError — пользователь закрыл шторку, ничего не выбрав. Это не сбой.
        if ((e as { name?: string })?.name !== 'AbortError') {
          console.warn('[ChatShare] native share failed', { id: session.id, error: String(e) });
          showToast(t('sidebar.shareFailed'));
        }
      }
      return;
    }

    try {
      await navigator.clipboard.writeText(url);
      showToast(t('sidebar.shareLinkCopied'));
    } catch (e) {
      console.warn('[ChatShare] could not copy the link', { id: session.id, error: String(e) });
      showToast(t('sidebar.shareFailed'));
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
    // REGENERATE_REPLACES_TURN: добавлено 2026-09-24 (C-10) — the turn replaces the chat's last
    // stored turn (regenerate / resend / edit of the last user message).
    regenerate?: boolean,
  ): Promise<SendOutcome> => {
    const live = liveRef.current;
    const turns = registryRef.current!;
    const session = live.sessions.find((s) => s.id === sessionId);
    // SESSION_ISOLATION: a reply to a request of a previous account must not touch this one.
    const ownerAtStart = storeUserRef.current;

    // TURN_GUARD (H6): один ход на чат. Раньше Enter во время генерации отправлял второй ход с тем же
    // taskId: бэкенд молча его выбрасывал, хвост ответа 1 лился в пузырь 2, а пузырь 1 навсегда
    // оставался «генерирующимся».
    if (turns.bySession(sessionId) || isSessionStreaming(session)) return { ok: false, reason: 'busy' };
    if (session?.forbidden) return { ok: false, reason: 'forbidden' };
    if (session?.needsTranscript) return { ok: false, reason: 'loading' };

    const kind = session?.kind ?? live.activeTab;
    const continueTarget = continueMessageId
      ? session?.messages.find((m) => m.id === continueMessageId)
      : undefined;
    const continueFrom = continueTarget?.content;
    const previousSessionStatus = session?.status ?? 'Idle';

    const assistantId = continueMessageId ?? uid();
    // Registered BEFORE the first await, so a second Enter in the same tick is already refused.
    const ctx: TurnContext = { sessionId, messageId: assistantId, startedAt: Date.now() };
    turns.begin(ctx);

    // ATTACHMENTS_IN_BUBBLE: turn the outgoing files into the transcript representation (image
    // thumbnails, file chips). Only the preview is kept — the originals go to the model.
    let messageAttachments: Awaited<ReturnType<typeof toMessageAttachment>>[] = [];
    try {
      messageAttachments = attachments.length
        ? await Promise.all(attachments.map((a) => toMessageAttachment(a)))
        : [];
    } catch (e) {
      console.warn('[Attachments] could not build previews', String(e));
    }

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
      id: assistantId,
      role: 'assistant',
      content: '',
      thinking: '',
      status: 'streaming',
      logs: [],
      screenshots: [],
      createdAt: Date.now(),
      model: live.model,
      awaitingTaskId: true,
    };

    setSessions((prev) =>
      updateSession(prev, sessionId, (s) => ({
        ...s,
        title: s.title === defaultTitle(kind, live.t) ? prompt.slice(0, 40) : s.title,
        status: 'Running',
        model: live.model,
        messages: continueMessageId
          ? s.messages.map((m) =>
              m.id === continueMessageId
                ? { ...m, status: 'streaming', error: undefined, taskId: undefined, awaitingTaskId: true }
                : m,
            )
          : appendUserMessage
            ? [...s.messages, userMsg, assistantMsg]
            : [...s.messages, assistantMsg],
      })),
    );

    /** Takes the optimistic messages back out (the turn never reached the model). */
    const rollback = (status: ChatSession['status']) => {
      setSessions((prev) =>
        updateSession(prev, sessionId, (s) => ({
          ...s,
          status,
          messages: continueMessageId
            ? s.messages.map((m) =>
                m.id === continueMessageId
                  ? { ...m, status: continueTarget?.status ?? 'stopped', taskId: continueTarget?.taskId, awaitingTaskId: undefined }
                  : m,
              )
            : s.messages.filter((m) => m.id !== userMsg.id && m.id !== assistantMsg.id),
        })),
      );
    };

    try {
      // TURN_SCOPE (H6): sessionId больше не отправляется. Бэкенд превращал его в taskId, поэтому все
      // ходы чата жили в ОДНОЙ группе task_{id}, и события соседних ходов было не различить. Теперь у
      // каждого хода свой taskId; рабочая область и история по-прежнему ключуются chatId.
      const res = await runTask({
        model: live.model,
        prompt,
        attachments: attachments.length ? attachments : undefined,
        thinking: live.thinking,
        reasoningEffort: live.reasoningEffort,
        smartSearch: live.smartSearch,
        studentsMode: live.students,
        chatId: sessionId,
        // INCOGNITO_CHAT: добавлено 2026-09-20 — 'incognitoActive' covers the very first
        // message (the session does not exist yet); later turns read the flag off the session.
        incognito: live.incognitoActive || live.sessionIncognito,
        // CONTINUE_GENERATION: the partial answer is handed back so the model finishes it.
        assistantPrefix: continueFrom?.trim() ? continueFrom : undefined,
        // CHAT_KIND_SYNC: режим вкладки сохраняется вместе с историей, чтобы на другом устройстве
        // чат учеников открылся в «Учениках», а не в общем чате.
        chatKind: live.sessions.find((s) => s.id === sessionId)?.kind ?? live.activeTab,
        regenerate: regenerate ? true : undefined,
      });

      const taskId = res.id;
      if (storeUserRef.current !== ownerAtStart) return { ok: true };
      setSessions((prev) =>
        updateSession(prev, sessionId, (s) => ({
          ...s,
          taskId,
          remote: true,
          // TRANSCRIPT_REFRESH (M25): the server's next change to this chat is our own.
          unsyncedTurns: (s.unsyncedTurns ?? 0) + 1,
          messages: s.messages.map((m) => (m.id === assistantId ? { ...m, taskId, awaitingTaskId: undefined } : m)),
        })),
      );

      if (ctx.stopRequested || !turns.isActive(ctx)) {
        // Stop was pressed (or the chat deleted) while the request was in flight: the turn is already
        // closed on screen — cancel it on the server too.
        turns.end(ctx);
        void opsRef.current.stopOnServer({ sessionId, messageId: assistantId, taskId });
        return { ok: true };
      }

      turns.attachTask(ctx, taskId);
      await opsRef.current.joinTurnGroups(ctx);
      return { ok: true };
    } catch (e) {
      turns.end(ctx);
      if (storeUserRef.current !== ownerAtStart) return { ok: false };
      const status = httpStatus(e);
      const code = errorCode(e);

      // SUBSCRIPTION_TIERS: добавлено 2026-09-17
      const data = (e as { response?: { data?: { error?: string; limit?: string; resetsAt?: string } } })?.response?.data;
      if (code === 'LIMIT_EXCEEDED') {
        setLimitExceeded({ limit: data?.limit ?? 'unknown', resetsAt: data?.resetsAt ?? '' });
        setUpgradeOpen(true);
        setSessions((prev) =>
          updateSession(prev, sessionId, (s) => ({
            ...s,
            status: 'Failed',
            messages: s.messages.map((m) =>
              m.id === assistantMsg.id ? { ...m, status: 'error', error: live.t('agent.limitExceeded'), awaitingTaskId: undefined } : m,
            ),
          })),
        );
        void live.refreshUsage();
        return { ok: false };
      }

      // TURN_GUARD (C-2): 409 — в этом чате ещё идёт ход (другая вкладка/устройство). 403 — чат
      // чужой. Ни то, ни другое не ответ модели: оптимистичные сообщения убираются, текст и файлы
      // возвращаются в композер.
      if (status === 409 || code === 'TURN_IN_FLIGHT') {
        rollback(previousSessionStatus === 'Running' ? 'Idle' : previousSessionStatus);
        return { ok: false, reason: 'busy' };
      }
      if (status === 403 || code === 'CHAT_FORBIDDEN') {
        rollback(previousSessionStatus === 'Running' ? 'Idle' : previousSessionStatus);
        opsRef.current.markChatForbidden(sessionId);
        return { ok: false, reason: 'forbidden' };
      }

      // ATTACHMENT_SIZE_LIMIT: добавлено 2026-09-22 — прокси (nginx, дефолт 1MB) режет тело
      // раньше бэкенда и отдаёт голый 413. Это не ошибка модели, а неудавшаяся отправка,
      // поэтому оптимистичные сообщения убираем из ленты, а текст и файлы возвращает композер.
      if (status === 413 && appendUserMessage) {
        rollback('Idle');
        return { ok: false, tooLarge: true };
      }

      const message = humanError(e, live.t);
      setSessions((prev) =>
        updateSession(prev, sessionId, (s) => ({
          ...s,
          status: 'Failed',
          messages: s.messages.map((m) =>
            m.id === assistantMsg.id ? { ...m, status: 'error', error: message, awaitingTaskId: undefined } : m,
          ),
        })),
      );
      // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
      // liveRespondRef.current?.reject(new Error(message));
      return { ok: false };
    }
  }, []);

  /** Refusals of the bubble actions (regenerate, resend, edit, continue) are said out loud. */
  const notifyOutcome = useCallback((outcome: SendOutcome) => {
    if (outcome.ok) return;
    const live = liveRef.current;
    if (outcome.reason === 'busy') live.showToast(live.t('sync.turnInFlight'));
    else if (outcome.reason === 'forbidden') live.showToast(live.t('sync.chatForbidden'));
    else if (outcome.reason === 'loading') live.showToast(live.t('sync.loadingChat'));
  }, []);

  async function handleSend(prompt: string, attachments: TaskAttachment[]): Promise<SendOutcome> {
    if (!prompt.trim() || !token) return { ok: false };
    // TURN_GUARD (H6): как и у regenerate/resend/edit — пока в открытом чате идёт ход, новый не
    // отправляется (текст остаётся в композере).
    if (activeSession && (registry.bySession(activeSession.id) || isSessionStreaming(activeSession))) {
      return { ok: false, reason: 'busy' };
    }
    const sessionId = ensureSessionId(activeTab);
    return startCompletion(sessionId, prompt, attachments, true);
  }

  // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17 (sendLiveMessage удалён вместе с
  // глобальным streamingRef; при возврате голосового режима строить его поверх startCompletion).

  /** True while a turn of this chat is being generated (this tab, or another tab via storage). */
  const sessionBusy = useCallback((s: ChatSession) =>
    Boolean(registryRef.current?.bySession(s.id)) || isSessionStreaming(s), []);

  // BUGFIX_PERF: stable callbacks (see liveRef) so MessageBubble's memo is not defeated.
  const handleRegenerate = useCallback((assistantMessageId: string) => {
    const live = liveRef.current;
    if (!live.token) return;
    const session = live.sessions.find((s) => s.id === live.activeId);
    if (!session) return;
    if (sessionBusy(session)) {
      live.showToast(live.t('sync.turnInFlight'));
      return;
    }
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

    // C-10: only the answer of the chat's LAST turn can replace that turn on the server.
    const regenerate = !session.incognito && idx === session.messages.length - 1 && turnPersisted(session.messages[idx]);
    void startCompletion(session.id, prompt, [], false, undefined, regenerate).then(notifyOutcome);
  }, [startCompletion, notifyOutcome, sessionBusy]);

  const handleResend = useCallback((messageId: string) => {
    const live = liveRef.current;
    if (!live.token) return;
    const session = live.sessions.find((s) => s.id === live.activeId);
    if (!session) return;
    if (sessionBusy(session)) {
      live.showToast(live.t('sync.turnInFlight'));
      return;
    }
    const index = session.messages.findIndex((m) => m.id === messageId);
    const message = session.messages[index];
    if (!message || message.role !== 'user') return;
    const regenerate = !session.incognito && replacesLastTurn(session.messages, index);
    void startCompletion(session.id, message.content, [], true, undefined, regenerate).then(notifyOutcome);
  }, [startCompletion, notifyOutcome, sessionBusy]);

  const handleEditMessage = useCallback((messageId: string, newContent: string) => {
    const live = liveRef.current;
    if (!live.token) return;
    const session = live.sessions.find((s) => s.id === live.activeId);
    if (!session) return;
    if (sessionBusy(session)) {
      live.showToast(live.t('sync.turnInFlight'));
      return;
    }
    const index = session.messages.findIndex((m) => m.id === messageId);
    const message = session.messages[index];
    if (!message || message.role !== 'user') return;
    const regenerate = !session.incognito && replacesLastTurn(session.messages, index);
    void startCompletion(session.id, newContent, [], true, undefined, regenerate).then(notifyOutcome);
  }, [startCompletion, notifyOutcome, sessionBusy]);

  // CONTINUE_GENERATION: добавлено 2026-09-21
  // BUGFIX_CONTINUE_CLICK: добавлено 2026-09-22 — раньше здесь были «тихие» return'ы (нет
  // токена, идёт стрим, пустой частичный ответ), из-за которых клик по «Продолжить» выглядел
  // как полностью мёртвая кнопка. Теперь каждый отказ либо логичен, либо виден пользователю.
  // Resumes a stopped answer: the already-streamed text goes back to the model as its own
  // truncated turn, and the reply keeps growing inside the very same message.
  const handleContinue = useCallback((messageId: string) => {
    const live = liveRef.current;
    if (!live.token) return;

    // Prefer the session currently on screen, then fall back to whichever session owns it.
    const session =
      live.sessions.find(
        (s) => s.id === live.activeId && s.messages.some((m) => m.id === messageId),
      ) ?? live.sessions.find((s) => s.messages.some((m) => m.id === messageId));
    if (!session) return;

    if (sessionBusy(session)) {
      // Something is already running in this chat — say so instead of swallowing the click.
      live.showToast(live.t('message.continueBusy'));
      return;
    }

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
    void startCompletion(session.id, prompt, [], false, messageId).then(notifyOutcome);
  }, [startCompletion, notifyOutcome, sessionBusy]);

  // STOP_CONFIRM: изменено 2026-09-24 (M17) — «Стоп» останавливает ход ОТКРЫТОГО чата: экран
  // переключается сразу, а ответ хаба решает, что дальше (см. stopOnServer).
  function handleStop() {
    const session = activeSession;
    if (!session) return;
    const ctx = registry.bySession(session.id);
    const last = session.messages[session.messages.length - 1];
    const messageId = ctx?.messageId ?? (isSessionStreaming(session) ? last.id : null);
    if (!messageId) return;

    // Without a live context (a turn another tab runs, or one left over) the id comes off the message.
    const taskId = ctx
      ? ctx.taskId
      : last.taskId ?? (last.awaitingTaskId ? undefined : session.taskId);

    if (ctx) {
      ctx.stopRequested = true;
      registry.end(ctx);
    }
    // CONTINUE_GENERATION: the stop marker is no longer baked into the text — it is rendered
    // from the 'stopped' status, so the stored content stays exactly the partial answer that
    // "Продолжить" hands back to the model.
    finalizeTurn({ sessionId: session.id, messageId }, 'stopped');
    clearTaskApprovals(taskId);

    // No task id yet: POST /run is still in flight — startCompletion cancels the turn right after
    // the server returns its id (ctx.stopRequested).
    if (taskId) void stopOnServer({ sessionId: session.id, messageId, taskId });
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
    const taskId = live.pendingAction?.taskId ?? registryRef.current?.bySession(live.activeId)?.taskId ?? live.sessionTaskId;
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

  // CHAT_SHARE: открыть чат, пришедший ссылкой. Ждём, пока синхронизация подтянет список, —
  // на первом рендере чата ещё нет, а ссылку без него открыть нечем. Если чат так и не появился
  // (ссылка от чужого аккаунта), ничего не делаем: текущий экран остаётся на месте.
  useEffect(() => {
    const linked = chatIdFromHash(route);
    if (!linked || openedLinkRef.current === linked) return;

    const session = sessions.find((s) => s.id.toLowerCase() === linked.toLowerCase());
    if (!session) {
      // CHAT_SHARE_LINK: добавлено 2026-09-24 — синхронизация приносит только первые N чатов, а ссылка
      // может вести на более старый. Спрашиваем сервер об этом одном чате; чужой чат (404) — не открываем.
      if (token && linkFetchRef.current !== linked) {
        linkFetchRef.current = linked;
        const userAtStart = storeUserRef.current;
        void getChat(linked)
          .then((chat) => {
            if (!chat || storeUserRef.current !== userAtStart) return;
            const stub = sessionFromServer(chat, null, defaultTitle(kindFromServer(chat.kind), liveRef.current.t));
            setSessions((prev) =>
              prev.some((s) => s.id.toLowerCase() === stub.id.toLowerCase()) ? prev : [...prev, stub],
            );
          })
          .catch((e: unknown) => console.warn('[ChatShare] could not load the linked chat', { chatId: linked, error: String(e) }));
      }
      return;
    }

    openedLinkRef.current = linked;
    setActiveId(session.id);
    setActiveTab(session.kind ?? 'chat');
    // CHAT_MODEL_SYNC (M18): Cowork-чат по ссылке открывается как Cowork, flash/pro — как были.
    applySessionModel(session);
    setIncognito(false);
    if (isMobile) setSidebarOpen(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [route, sessions, isMobile]);

  // MOBILE_DRAWER: the sidebar is a drawer on phones, so it can be pulled out from the left edge and
  // pushed back with a swipe. The hook writes the transform straight to the DOM while dragging.
  useDrawerSwipe({
    enabled: isMobile,
    open: sidebarOpen,
    onOpen: () => setSidebarOpen(true),
    onClose: () => setSidebarOpen(false),
    panelRef: sidebarPanelRef,
    backdropRef: sidebarBackdropRef,
  });

  // MOBILE_KEYBOARD: keeps the composer above the on-screen keyboard where the viewport meta hint is
  // not supported (Safari); on Android the hint already resizes the layout.
  useKeyboardInset(isMobile);

  // ADMIN_HOOKS_ORDER: возвраты для админки обязаны стоять ПОСЛЕ самого последнего хука этого
  // компонента. Раньше они были выше useEffect'а инкогнито, и переход на `#/admin` (без F5)
  // рендерил App с на один хук меньше — React падал с #300 «Rendered fewer hooks than expected».
  // Правило на будущее: любой новый хук добавляй ВЫШЕ этого блока, а не после возврата чата.
  //
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
        asideRef={sidebarPanelRef}
        onToggle={() => setSidebarOpen((o) => !o)}
        onTabChange={(tab) => guardLeave(() => {
          handleTabChange(tab);
          if (isMobile) setSidebarOpen(false);
        })}
        onNewSession={(kind) => guardLeave(() => {
          handleNewSession(kind);
          if (isMobile) setSidebarOpen(false);
        })}
        onSelectSession={(id) => (id === activeId ? openSession(id) : guardLeave(() => openSession(id)))}
        onSearchChange={setSearch}
        onShareSession={handleShareSession}
        onPinSession={handlePinSession}
        onRenameSession={handleRenameSession}
        onDeleteSession={(id) => (id === activeId ? guardLeave(() => void handleDeleteSession(id)) : void handleDeleteSession(id))}
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
      {isMobile && (
        // MOBILE_DRAWER: mounted even while closed, because the drawer swipe fades it in
        // proportionally to the finger's travel. `pointer-events` is off until it is actually open.
        <div
          ref={sidebarBackdropRef}
          className={`sidebar-backdrop ${sidebarOpen ? 'sidebar-backdrop--on' : ''}`}
          onClick={() => setSidebarOpen(false)}
        />
      )}

      <main className="main" ref={mainRef}>
        <div className={isAgent ? 'main__body main__body--ide' : 'main__body'}>
          <div
            className={`${isAgent && !ideCollapsed && !isMobile ? 'ide-chat' : 'ide-chat--single'} chat-drop-target`}
            style={isAgent && !ideCollapsed && !isMobile ? { flex: `0 0 ${100 - workspaceWidth}%` } : undefined}
            {...dropZone.handlers}
          >
            {/* FILE_DROP: добавлено 2026-09-24 (L11) — подсказка поверх колонки чата. */}
            {dropZone.active && (
              <div className="chat-drop-overlay" aria-hidden="true">
                <span className="chat-drop-overlay__text">{t('sync.dropFiles')}</span>
              </div>
            )}
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
              // FOCUS_MODE: mobile only, and never for guests — their hero holds the login and
              // registration buttons, which must not hide the moment the composer is tapped.
              composerFocused={isMobile && stageEmpty && composerFocused && !isGuest}
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
                    // STREAM_FOLLOW: agent tabs keep the tail in view (live logs/steps); plain chats
                    // must not drag the view while the answer streams in.
                    followStream={isAgent}
                    loading={activeLoading}
                    loadFailed={activeLoadFailed}
                    onRetryLoad={() => {
                      if (!activeSession) return;
                      setTranscriptLoad(null);
                      loadTranscript(activeSession.id);
                    }}
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
                    disabled={!token || activeLoading}
                    isGenerating={agentRunning}
                    onStop={handleStop}
                    // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
                    // onOpenLive={() => setIsLiveOpen(true)}
                    onSend={handleSend}
                    externalFiles={droppedFiles}
                    onComposerFocus={() => setComposerFocused(true)}
                    onComposerBlur={() => setComposerFocused(false)}
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
                onDirtyChange={setWorkspaceDirty}
              />
            </>
          )}
        </div>
        <StatusBar agentStatus={agentStatus} activity={agentActivity} cursor={cursorInfo} />
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

      {/* WORKSPACE_DIRTY: добавлено 2026-09-24 — подтверждение ухода из чата с несохранёнными файлами. */}
      {pendingLeave && (
        <ConfirmDialog
          title={t('workspace.unsavedChanges')}
          message={t('sync.leaveChatUnsaved')}
          confirmLabel={t('sync.leaveWithoutSaving')}
          danger
          onConfirm={() => {
            const action = pendingLeave;
            setPendingLeave(null);
            setWorkspaceDirty(false);
            action();
          }}
          onCancel={() => setPendingLeave(null)}
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
