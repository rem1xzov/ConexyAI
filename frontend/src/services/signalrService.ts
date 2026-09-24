import * as signalR from '@microsoft/signalr';
import { recoverFromUnauthorized } from '../api/client';
import type { AgentStatusPayload, BuildProblemsPayload, FileCreatedPayload, PendingActionPayload, RunProjectErrorPayload, RunProjectResult, SearchStatusPayload, SignalrCallbacks, StopGenerationResult, SupportMessagePayload, TaskCompletedPayload, TerminalOutputPayload, TodoUpdatePayload, ToolActionEvent } from '../types/signalr';

export type ConnectionStatus =
  | 'connected'
  | 'connecting'
  | 'reconnecting'
  // SIGNALR_RESILIENCE: добавлено 2026-09-22 — переподключение идёт слишком долго (прокси/бэкенд
  // недоступны). Отдельное состояние, чтобы UI показал это явно, а не только бесконечный спиннер.
  | 'reconnect-failed'
  | 'disconnecting'
  | 'disconnected';

// SIGNALR_RESILIENCE: добавлено 2026-09-22
// Дефолтная политика withAutomaticReconnect делает всего 4 попытки (~42s) и навсегда сдаётся:
// onclose, и дальше клиент молча ждёт события, которые уже не придут. Для долгой задачи агента это
// и давало «вечное думает». Политика ниже не сдаётся никогда, а задержка растёт экспоненциально
// до 30s со случайным разбросом, чтобы не бить в лежащий бэкенд синхронно со всех вкладок.
const RECONNECT_MAX_DELAY_MS = 30_000;
// После этого времени непрерывных неудач UI говорит пользователю обновить страницу.
const RECONNECT_STALLED_AFTER_MS = 60_000;
// SIGNALR_RELIABILITY: добавлено 2026-09-23
// Сколько ждать, пока идущий процесс (пере)соединения сам завершится, прежде чем что-то трогать.
const CONNECT_SETTLE_WAIT_MS = 5_000;
// Бюджет на ПЕРВИЧНОЕ соединение. `withAutomaticReconnect` покрывает только уже установленное
// соединение: если первый `start()` падает (например negotiate отвечает 502, пока бэкенд
// пересоздаётся), клиент раньше молчал навсегда — до ручной перезагрузки страницы.
const INITIAL_CONNECT_DEADLINE_MS = 60_000;

function backoffDelay(attempt: number): number {
  const capped = Math.min(attempt, 5);
  const base = Math.min(1000 * 2 ** capped, RECONNECT_MAX_DELAY_MS);
  return base + Math.floor(Math.random() * 500);
}

/** Human-readable message for the connection logs (thrown values are not always Error instances). */
function describeError(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

// SESSION_REVOKED: добавлено 2026-09-24 (M19) — negotiate с отозванным токеном отвечает 401.
// SignalR заворачивает его в FailedToNegotiateWithServerError с текстом "... Status code '401'".
// Повторять подключение тем же токеном бессмысленно — это бесконечный цикл переподключений.
function isUnauthorizedConnectError(err: unknown): boolean {
  if (err instanceof signalR.HttpError) return err.statusCode === 401;
  const message = describeError(err);
  return /status code '401'/i.test(message) || /\bunauthorized\b/i.test(message);
}

// CHAT_OWNERSHIP: добавлено 2026-09-24 — контракт C-6. Хаб бросает HubException со словом
// «forbidden», если id задачи/чата принадлежит другому пользователю.
function isForbiddenHubError(err: unknown): boolean {
  return describeError(err).toLowerCase().includes('forbidden');
}

/** Thrown by {@link SignalrService.joinTask} when the server refused the group for good (C-6). */
export class ForbiddenJoinError extends Error {
  constructor(readonly id: string) {
    super(`Joining task_${id} is forbidden for this account`);
    this.name = 'ForbiddenJoinError';
  }
}

export function isForbiddenJoinError(err: unknown): err is ForbiddenJoinError {
  return err instanceof ForbiddenJoinError;
}

function toStatus(state: signalR.HubConnectionState): ConnectionStatus {
  switch (state) {
    case signalR.HubConnectionState.Connected:
      return 'connected';
    case signalR.HubConnectionState.Connecting:
      return 'connecting';
    case signalR.HubConnectionState.Reconnecting:
      return 'reconnecting';
    case signalR.HubConnectionState.Disconnecting:
      return 'disconnecting';
    default:
      return 'disconnected';
  }
}

/**
 * Sole owner of the raw SignalR connection. UI components never touch
 * @microsoft/signalr directly; they go through this manager.
 */
class SignalrService {
  private connection: signalR.HubConnection | null = null;
  private token: string | null = null;
  private callbacks: SignalrCallbacks = {};
  private terminalOutputHandlers = new Map<string, (data: string) => void>();
  private stateListeners = new Set<(state: ConnectionStatus) => void>();
  // SIGNALR_RESILIENCE: когда началась текущая серия неудачных переподключений (0 — не идёт).
  private reconnectStartedAt = 0;
  private stalledTimer: number | null = null;
  // SIGNALR_RELIABILITY: сколько прямо сейчас идёт вызовов start() (startWithRetry). Пока этот
  // счётчик не ноль, никто другой не имеет права вызывать start() — второй вызов на том же
  // соединении бросает исключение, и вместо восстановления связи получался бы новый разрыв.
  private startInFlight = 0;
  // Groups the client is supposed to belong to. Re-joined after every (re)connect so a
  // stale/restarted connection never silently drops the stream (the connection id changes
  // on reconnect, but SignalR groups are per-connection and must be re-added).
  private joinedTaskIds = new Set<string>();
  // SUPPORT_REJOIN: добавлено 2026-09-24 (M16) — группы тикетов поддержки тоже живут на уровне
  // соединения. Раньше после переподключения их никто не восстанавливал, и ответы саппорта
  // молча переставали приходить.
  private joinedSupportTicketIds = new Set<string>();
  // CHAT_OWNERSHIP: добавлено 2026-09-24 (C-6) — id, в которые сервер отказал навсегда. Повторный
  // JoinTask по ним (сторож, rejoin после reconnect) был бы бесконечным циклом отказов.
  private forbiddenIds = new Set<string>();
  // SUPPORT: добавлено 2026-09-19 — live subscribers for support messages (the support chat
  // opens/closes dynamically, so it subscribes/unsubscribes instead of using the static callbacks).
  private supportHandlers = new Set<(payload: SupportMessagePayload) => void>();
  // SESSION_REVOKED: одна обработка 401 за раз.
  private unauthorizedInFlight = false;

  async connect(token: string, callbacks: SignalrCallbacks): Promise<void> {
    this.token = token;
    this.callbacks = callbacks;

    const current = this.connection?.state;
    if (current === signalR.HubConnectionState.Connected) {
      await this.rejoinGroups();
      return;
    }

    // SIGNALR_RELIABILITY: добавлено 2026-09-23 — не трогать соединение, чьё (пере)соединение уже
    // идёт. Раньше здесь безусловно вызывался disconnect() → stop(), и это выбрасывало здоровое
    // переподключение на помойку (клиент потом заново проходил negotiate и получал 502).
    if (
      current === signalR.HubConnectionState.Connecting ||
      current === signalR.HubConnectionState.Reconnecting
    ) {
      const settled = await this.waitForSettled(this.connection!, CONNECT_SETTLE_WAIT_MS);
      if (settled === signalR.HubConnectionState.Connected) {
        await this.rejoinGroups();
        return;
      }
    }

    await this.teardown();

    this.connection = this.build();
    this.registerCallbacks();
    await this.startWithRetry();
  }

  // SESSION_ISOLATION: добавлено 2026-09-24 (H10) — обновление токена того же пользователя не
  // требует переподключения: accessTokenFactory читает this.token при каждом negotiate/reconnect.
  // Раньше каждое обновление токена рвало соединение вместе с подписками на идущий ход.
  setToken(token: string | null): void {
    this.token = token;
  }

  async joinTask(taskId: string): Promise<void> {
    // CHAT_OWNERSHIP: сервер уже отказал — не долбим его снова (C-6).
    if (this.forbiddenIds.has(taskId)) throw new ForbiddenJoinError(taskId);
    this.joinedTaskIds.add(taskId);
    await this.ensureConnected();
    // The (re)connect that ensureConnected waited for may have tried this id already (rejoinGroups).
    if (this.forbiddenIds.has(taskId)) throw new ForbiddenJoinError(taskId);
    console.log('[signalr] joinTask', taskId, 'connId=', this.connection?.connectionId);
    try {
      await this.connection!.invoke('JoinTask', taskId);
    } catch (err) {
      if (isForbiddenHubError(err)) throw this.markForbidden(taskId);
      throw err;
    }
  }

  // TASK_COMPLETION_WATCHDOG: добавлено 2026-09-22 — повторная подписка нужна только если её нет,
  // иначе сторож опрашивал бы хаб каждые несколько секунд без причины.
  async ensureGroup(taskId: string): Promise<void> {
    if (this.forbiddenIds.has(taskId)) throw new ForbiddenJoinError(taskId);
    if (this.joinedTaskIds.has(taskId) && this.connection?.state === signalR.HubConnectionState.Connected) {
      return;
    }
    await this.joinTask(taskId);
  }

  /** True when the server has refused this task/chat id for the current account (C-6). */
  isForbidden(id: string): boolean {
    return this.forbiddenIds.has(id);
  }

  async leaveTask(taskId: string): Promise<void> {
    this.joinedTaskIds.delete(taskId);
    if (!this.connection || this.connection.state !== signalR.HubConnectionState.Connected) return;
    await this.connection.invoke('LeaveTask', taskId);
  }

  // SESSION_ISOLATION: добавлено 2026-09-24 (H10) — выход из аккаунта. Раньше disconnect() оставлял
  // в памяти JWT прошлого пользователя и его группы: следующий вход в том же браузере
  // переподписывался на чужие задачи и ходил в хаб со старым токеном.
  // Состояние чистится ДО await: connect() нового пользователя, вызванный сразу следом (без
  // ожидания), иначе получил бы свой токен и колбэки затёртыми продолжением этого метода.
  async disconnect(): Promise<void> {
    this.token = null;
    this.callbacks = {};
    this.joinedTaskIds.clear();
    this.joinedSupportTicketIds.clear();
    this.forbiddenIds.clear();
    await this.teardown();
  }

  /** Stops the current connection without forgetting who the user is (used by connect()). */
  private async teardown(): Promise<void> {
    // SIGNALR_RELIABILITY: обнуляем ссылку ДО stop(), чтобы никто не увидел полузакрытое
    // соединение как рабочее; stop() же может бросить (например если шёл start()).
    const conn = this.connection;
    this.connection = null;
    if (conn) {
      try {
        await conn.stop();
      } catch (err) {
        console.warn('[signalr] stop() failed', describeError(err));
      }
    }
    this.clearReconnectState();
    this.notifyState('disconnected');
  }

  get state(): signalR.HubConnectionState {
    return this.connection?.state ?? signalR.HubConnectionState.Disconnected;
  }

  /** Subscribe to connection-status changes; fires immediately with the current status. */
  onStateChange(cb: (state: ConnectionStatus) => void): () => void {
    this.stateListeners.add(cb);
    cb(toStatus(this.state));
    return () => {
      this.stateListeners.delete(cb);
    };
  }

  // ---- Interactive terminal (pty) ----

  /** Subscribes to pty output for a single session; returns an unsubscribe function. */
  onTerminalOutput(sessionId: string, handler: (data: string) => void): () => void {
    this.terminalOutputHandlers.set(sessionId, handler);
    return () => {
      if (this.terminalOutputHandlers.get(sessionId) === handler) {
        this.terminalOutputHandlers.delete(sessionId);
      }
    };
  }

  async startTerminal(sessionId: string): Promise<void> {
    await this.ensureConnected();
    await this.connection!.invoke('StartTerminal', sessionId);
  }

  async sendTerminalInput(sessionId: string, data: string): Promise<void> {
    if (!this.connection) return;
    await this.connection.invoke('SendInput', sessionId, data);
  }

  async resizeTerminal(sessionId: string, cols: number, rows: number): Promise<void> {
    if (!this.connection) return;
    await this.connection.invoke('ResizeTerminal', sessionId, cols, rows);
  }

  async stopTerminal(sessionId: string): Promise<void> {
    if (!this.connection) return;
    await this.connection.invoke('StopTerminal', sessionId);
  }

  // STOP_CONFIRM: изменено 2026-09-24 (M17, контракт C-5) — хаб теперь подтверждает остановку.
  /**
   * Cancels an in-flight generation (any model) for the given task id and reports what the server
   * did. An older backend that returns nothing is treated as `stopping`.
   */
  async stopGeneration(taskId: string): Promise<StopGenerationResult> {
    await this.ensureConnected();
    const result = await this.connection!.invoke<string | null>('StopGeneration', taskId);
    return result === 'cancelled' || result === 'not_found' ? result : 'stopping';
  }

  // COMMAND_CONFIRM: расширено 2026-09-20 — подтверждение требуется для любой bash-команды.
  /**
   * Resolves a paused bash command with the user's decision. When `approveAll` is set on an
   * approval, every later command of the same task runs without asking again.
   */
  async confirmAction(actionId: string, approved: boolean, approveAll = false): Promise<boolean> {
    await this.ensureConnected();
    return await this.connection!.invoke<boolean>('ConfirmAction', actionId, approved, approveAll);
  }

  /** Turns "allow all commands for this task" on or off while the agent task is running. */
  async setTaskAutoApproval(taskId: string, enabled: boolean): Promise<void> {
    await this.ensureConnected();
    await this.connection!.invoke('SetTaskAutoApproval', taskId, enabled);
  }

  // SUPPORT: добавлено 2026-09-19
  async joinSupportTicket(ticketId: string): Promise<void> {
    // SUPPORT_REJOIN: запоминаем тикет, чтобы rejoinGroups вернул его после переподключения.
    this.joinedSupportTicketIds.add(ticketId);
    await this.ensureConnected();
    await this.connection!.invoke('JoinSupportTicket', ticketId);
  }

  async leaveSupportTicket(ticketId: string): Promise<void> {
    this.joinedSupportTicketIds.delete(ticketId);
    if (!this.connection || this.connection.state !== signalR.HubConnectionState.Connected) return;
    await this.connection.invoke('LeaveSupportTicket', ticketId);
  }

  /** Subscribes to live support messages; returns an unsubscribe function. */
  onSupportMessage(cb: (payload: SupportMessagePayload) => void): () => void {
    this.supportHandlers.add(cb);
    return () => this.supportHandlers.delete(cb);
  }

  // ---- Run project (Ctrl+F5) ----

  async runProject(sessionId: string): Promise<RunProjectResult> {
    await this.ensureConnected();
    return await this.connection!.invoke<RunProjectResult>('RunProject', sessionId);
  }

  async runProjectWithCommand(sessionId: string, command: string): Promise<RunProjectResult> {
    await this.ensureConnected();
    return await this.connection!.invoke<RunProjectResult>('RunProjectWithCommand', sessionId, command);
  }

  async getPlatform(): Promise<string> {
    await this.ensureConnected();
    return await this.connection!.invoke<string>('GetPlatform');
  }

  private build(): signalR.HubConnection {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/conexy', {
        accessTokenFactory: () => this.token ?? '',
      })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (context) => this.nextReconnectDelay(context) })
      // Aligned with the server: it pings every 15s and waits 120s for us. The client's default
      // 30s server timeout was tight enough to declare a healthy connection dead mid-task.
      .withKeepAliveInterval(15_000)
      .withServerTimeout(120_000)
      .build();

    connection.onreconnecting(() => {
      if (this.reconnectStartedAt === 0) {
        this.reconnectStartedAt = Date.now();
      }
      this.notifyState('reconnecting');
      this.scheduleStalledCheck();
    });

    connection.onreconnected(() => {
      this.clearReconnectState();
      this.notifyState('connected');
      void this.rejoinGroups();
      // Let the app reconcile anything that happened while the socket was down.
      this.callbacks.onReconnected?.();
    });

    connection.onclose(() => {
      this.clearReconnectState();
      this.notifyState('disconnected');
    });

    return connection;
  }

  /** Reconnect policy: back off forever, except after a 401 — a revoked token never gets better. */
  private nextReconnectDelay(context: signalR.RetryContext): number | null {
    if (isUnauthorizedConnectError(context.retryReason)) {
      void this.handleUnauthorized();
      return null;
    }
    return backoffDelay(context.previousRetryCount);
  }

  // SESSION_REVOKED: добавлено 2026-09-24 (M19) — 401 на negotiate/reconnect идёт в ту же единую
  // точку, что и 401 REST-запросов: в Development — один тихий dev-токен того же пользователя и
  // новое соединение (группы переподключаются); иначе приложение выходит из аккаунта.
  private async handleUnauthorized(): Promise<void> {
    if (this.unauthorizedInFlight) return;
    this.unauthorizedInFlight = true;
    try {
      const next = await recoverFromUnauthorized(this.token, 'signalr');
      // Signed out meanwhile (disconnect() cleared the token): nothing to reconnect.
      if (next && this.token !== null) {
        this.token = next;
        await this.connect(next, this.callbacks);
      }
    } catch (err) {
      console.warn('[signalr] recovery after 401 failed', describeError(err));
    } finally {
      this.unauthorizedInFlight = false;
    }
  }

  // SIGNALR_RESILIENCE: переподключение может длиться долго (502 от прокси, перезапуск бэкенда).
  // Через минуту непрерывных неудач переводим UI в явное «не удалось восстановить», продолжая
  // попытки в фоне — так пользователь не гадает, завис агент или отвалилась связь.
  private scheduleStalledCheck(): void {
    if (this.stalledTimer !== null) return;
    this.stalledTimer = window.setInterval(() => {
      if (
        this.connection?.state === signalR.HubConnectionState.Reconnecting &&
        this.reconnectStartedAt > 0 &&
        Date.now() - this.reconnectStartedAt >= RECONNECT_STALLED_AFTER_MS
      ) {
        this.notifyState('reconnect-failed');
      }
    }, 5000);
  }

  private clearReconnectState(): void {
    this.reconnectStartedAt = 0;
    if (this.stalledTimer !== null) {
      window.clearInterval(this.stalledTimer);
      this.stalledTimer = null;
    }
  }

  private notifyState(state: ConnectionStatus): void {
    this.stateListeners.forEach((cb) => cb(state));
  }

  /** Re-adds this (now-current) connection to every previously joined group. */
  private async rejoinGroups(): Promise<void> {
    if (!this.connection || this.connection.state !== signalR.HubConnectionState.Connected) {
      return;
    }
    for (const taskId of [...this.joinedTaskIds]) {
      try {
        console.log('[signalr] rejoinTask', taskId, 'connId=', this.connection.connectionId);
        await this.connection.invoke('JoinTask', taskId);
      } catch (e) {
        // CHAT_OWNERSHIP: отказ по владельцу не лечится повтором — забываем группу (C-6).
        if (isForbiddenHubError(e)) {
          this.markForbidden(taskId);
          continue;
        }
        console.warn('[signalr] failed to rejoin group', taskId, e);
      }
    }
    // SUPPORT_REJOIN: добавлено 2026-09-24 (M16).
    for (const ticketId of [...this.joinedSupportTicketIds]) {
      try {
        await this.connection.invoke('JoinSupportTicket', ticketId);
      } catch (e) {
        if (isForbiddenHubError(e)) this.joinedSupportTicketIds.delete(ticketId);
        console.warn('[signalr] failed to rejoin support ticket', ticketId, describeError(e));
      }
    }
  }

  /** Remembers a refused id, drops it from the rejoin set and tells the app (C-6). */
  private markForbidden(id: string): ForbiddenJoinError {
    this.joinedTaskIds.delete(id);
    if (!this.forbiddenIds.has(id)) {
      this.forbiddenIds.add(id);
      console.warn('[signalr] JoinTask forbidden for this account; not retrying', { id });
      this.callbacks.onJoinForbidden?.(id);
    }
    return new ForbiddenJoinError(id);
  }

  private registerCallbacks(): void {
    if (!this.connection) return;

    // STREAM_SCOPE: добавлено 2026-09-24 (C-1) — последний аргумент каждого события группы — её id.
    this.connection.on('OnContentToken', (delta: string, scopeId?: string) => this.callbacks.onContentToken?.(delta, scopeId));
    this.connection.on('OnThinkingToken', (delta: string, scopeId?: string) => this.callbacks.onThinkingToken?.(delta, scopeId));
    this.connection.on('OnLog', (message: string, scopeId?: string) => this.callbacks.onLog?.(message, scopeId));
    this.connection.on('OnScreenshot', (base64: string, scopeId?: string) => this.callbacks.onScreenshot?.(base64, scopeId));
    this.connection.on('OnAgentStatus', (payload: AgentStatusPayload, scopeId?: string) => this.callbacks.onAgentStatus?.(payload, scopeId));
    this.connection.on('OnFileCreated', (payload: FileCreatedPayload, scopeId?: string) => this.callbacks.onFileCreated?.(payload, scopeId));
    this.connection.on('ToolAction', (event: ToolActionEvent, scopeId?: string) => this.callbacks.onToolAction?.(event, scopeId));
    this.connection.on('TodoUpdate', (payload: TodoUpdatePayload, scopeId?: string) => this.callbacks.onTodoUpdate?.(payload, scopeId));
    this.connection.on('OnCompleted', (payload: TaskCompletedPayload, scopeId?: string) => this.callbacks.onCompleted?.(payload, scopeId));
    this.connection.on('OnError', (error: string, scopeId?: string) => this.callbacks.onError?.(error, scopeId));
    this.connection.on('OnStopped', (taskId: string, scopeId?: string) => this.callbacks.onStopped?.(taskId, scopeId));
    this.connection.on('RunProjectError', (payload: RunProjectErrorPayload) => this.callbacks.onRunProjectError?.(payload));
    this.connection.on('SearchStatus', (payload: SearchStatusPayload, scopeId?: string) => this.callbacks.onSearchStatus?.(payload, scopeId));
    this.connection.on('BuildProblems', (payload: BuildProblemsPayload, scopeId?: string) => this.callbacks.onProblems?.(payload, scopeId));
    // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
    this.connection.on('OnPendingActionCreated', (payload: PendingActionPayload, scopeId?: string) => this.callbacks.onPendingActionCreated?.(payload, scopeId));
    // SUPPORT: добавлено 2026-09-19
    this.connection.on('OnSupportMessageReceived', (payload: SupportMessagePayload) => {
      this.callbacks.onSupportMessageReceived?.(payload);
      this.supportHandlers.forEach((h) => h(payload));
    });
    this.connection.on('TerminalOutput', (payload: TerminalOutputPayload) => {
      this.terminalOutputHandlers.get(payload.sessionId)?.(payload.data);
    });
  }

  private async ensureConnected(): Promise<void> {
    if (!this.connection) {
      this.connection = this.build();
      this.registerCallbacks();
    }
    const conn = this.connection;

    // SIGNALR_RELIABILITY: добавлено 2026-09-23.
    // Раньше здесь стояло `if (state !== Connected) await start()`. Это ГЛАВНАЯ причина, по которой
    // во время переподключения отваливалась вся работа через хаб — включая кнопку «Запустить»:
    // start() на соединении в состоянии Reconnecting бросает
    //   "Cannot start a HubConnection that is not in the 'Disconnected' state."
    // Поэтому и RunProject, и StopGeneration, и JoinTask падали ровно тогда, когда связь как раз
    // восстанавливалась. Теперь: дожидаемся, что идущий процесс сам разрешится, и только по-настоящему
    // разорванное соединение поднимаем вручную.
    const settled = await this.waitForSettled(conn, CONNECT_SETTLE_WAIT_MS);
    if (this.connection !== conn) return; // connect()/disconnect() заменили соединение, пока мы ждали

    if (settled === signalR.HubConnectionState.Connected) return;

    if (settled === signalR.HubConnectionState.Disconnected) {
      // Сюда попадаем, если соединение вообще не поднималось (первый start() ещё не вызван или
      // провалился). Ретрай-цикл живёт в connect(); здесь одна попытка, а ошибка должна дойти до
      // вызывающего, чтобы кнопка показала понятное сообщение, а не молчала.
      try {
        await conn.start();
      } catch (err) {
        if (isUnauthorizedConnectError(err)) void this.handleUnauthorized();
        throw err;
      }
      await this.rejoinGroups();
    }

    if (conn.state !== signalR.HubConnectionState.Connected) {
      this.notifyState(toStatus(conn.state));
      throw new Error('Подключение к серверу восстанавливается — повторите через несколько секунд.');
    }

    this.notifyState('connected');
  }

  /**
   * Waits until the connection leaves the transient Connecting/Reconnecting/Disconnecting states
   * (and until no start() of ours is in flight) and reports the state it settled in. Bounded, so a
   * wedged transport can never block a click handler forever.
   */
  private async waitForSettled(
    conn: signalR.HubConnection,
    timeoutMs: number,
  ): Promise<signalR.HubConnectionState> {
    const transient = [
      signalR.HubConnectionState.Connecting,
      signalR.HubConnectionState.Reconnecting,
      signalR.HubConnectionState.Disconnecting,
    ];
    const deadline = Date.now() + timeoutMs;

    for (;;) {
      if (this.connection !== conn) return conn.state; // replaced while we waited

      const state = conn.state;
      // A retry loop of ours owns start() right now: between attempts the state reads as
      // Disconnected, and acting on that would race the next start().
      const ownedByStartLoop = this.startInFlight > 0 && state === signalR.HubConnectionState.Disconnected;
      if (!transient.includes(state) && !ownedByStartLoop) return state;

      if (Date.now() >= deadline) return conn.state;
      await new Promise((resolve) => window.setTimeout(resolve, 200));
    }
  }

  /**
   * The FIRST connection needs its own retry loop: `withAutomaticReconnect` only covers a connection
   * that was established once, so a failing negotiate (502 while the backend is being redeployed)
   * used to leave the app permanently offline until a manual reload.
   *
   * Never rejects — callers fire this off and rely on the connection-status banner for feedback.
   */
  private async startWithRetry(): Promise<void> {
    const conn = this.connection;
    if (!conn) return;

    this.notifyState('connecting');
    const deadline = Date.now() + INITIAL_CONNECT_DEADLINE_MS;
    let attempt = 0;

    this.startInFlight += 1;
    try {
      for (;;) {
        if (this.connection !== conn) return; // replaced by connect()/disconnect() meanwhile

        try {
          await conn.start();
          this.clearReconnectState();
          this.notifyState('connected');
          console.info('[signalr] connected', { attempt, connectionId: conn.connectionId });
          await this.rejoinGroups();
          return;
        } catch (err) {
          // SESSION_REVOKED: 401 — повтор тем же токеном не поможет; решает единая обработка 401.
          if (isUnauthorizedConnectError(err)) {
            this.notifyState('disconnected');
            void this.handleUnauthorized();
            return;
          }
          attempt += 1;
          console.warn('[signalr] initial connect failed', { attempt, error: describeError(err) });

          if (Date.now() >= deadline) {
            console.error('[signalr] giving up on the initial connect after', attempt, 'attempt(s)');
            this.notifyState('reconnect-failed');
            return;
          }

          if (this.reconnectStartedAt === 0) this.reconnectStartedAt = Date.now();
          this.notifyState('reconnecting');
          this.scheduleStalledCheck();
          await new Promise((resolve) => window.setTimeout(resolve, backoffDelay(attempt)));
        }
      }
    } finally {
      this.startInFlight -= 1;
    }
  }

  // SANDBOX_TERMINAL: добавлено 2026-09-24 — хаб-методы терминала песочницы (контракт C-8:
  // GetTerminalMode / RunSandboxCommand / CancelSandboxCommand) вызываются из services/terminalHub.ts
  // через этот общий вызов, чтобы не расширять сервис под каждый новый метод.
  /** Invokes a hub method once the connection is up and returns its result. */
  async invokeHub<T = unknown>(method: string, ...args: unknown[]): Promise<T> {
    await this.ensureConnected();
    return await this.connection!.invoke<T>(method, ...args);
  }
}

export const signalrService = new SignalrService();
