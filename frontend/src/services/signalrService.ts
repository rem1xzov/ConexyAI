import * as signalR from '@microsoft/signalr';
import type { BuildProblemsPayload, PendingActionPayload, RunProjectErrorPayload, RunProjectResult, SearchStatusPayload, SignalrCallbacks, SupportMessagePayload, TerminalOutputPayload } from '../types/signalr';

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

function nextRetryDelay(context: signalR.RetryContext): number {
  const attempt = Math.min(context.previousRetryCount, 5);
  const base = Math.min(1000 * 2 ** attempt, RECONNECT_MAX_DELAY_MS);
  return base + Math.floor(Math.random() * 500);
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
  // Groups the client is supposed to belong to. Re-joined after every (re)connect so a
  // stale/restarted connection never silently drops the stream (the connection id changes
  // on reconnect, but SignalR groups are per-connection and must be re-added).
  private joinedTaskIds = new Set<string>();
  // SUPPORT: добавлено 2026-09-19 — live subscribers for support messages (the support chat
  // opens/closes dynamically, so it subscribes/unsubscribes instead of using the static callbacks).
  private supportHandlers = new Set<(payload: SupportMessagePayload) => void>();

  async connect(token: string, callbacks: SignalrCallbacks): Promise<void> {
    this.token = token;
    this.callbacks = callbacks;

    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.rejoinGroups();
      return;
    }

    await this.disconnect();

    this.connection = this.build();
    this.registerCallbacks();
    await this.connection.start();
    this.notifyState('connected');
    await this.rejoinGroups();
  }

  async joinTask(taskId: string): Promise<void> {
    this.joinedTaskIds.add(taskId);
    await this.ensureConnected();
    console.log('[signalr] joinTask', taskId, 'connId=', this.connection?.connectionId);
    await this.connection!.invoke('JoinTask', taskId);
  }

  async leaveTask(taskId: string): Promise<void> {
    this.joinedTaskIds.delete(taskId);
    if (!this.connection) return;
    await this.connection.invoke('LeaveTask', taskId);
  }

  async disconnect(): Promise<void> {
    if (this.connection) {
      await this.connection.stop();
      this.connection = null;
    }
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

  /** Cancels an in-flight generation (any model) for the given task id. */
  async stopGeneration(taskId: string): Promise<void> {
    await this.ensureConnected();
    await this.connection!.invoke('StopGeneration', taskId);
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
    await this.ensureConnected();
    await this.connection!.invoke('JoinSupportTicket', ticketId);
  }

  async leaveSupportTicket(ticketId: string): Promise<void> {
    if (!this.connection) return;
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
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: nextRetryDelay })
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
    for (const taskId of this.joinedTaskIds) {
      try {
        console.log('[signalr] rejoinTask', taskId, 'connId=', this.connection.connectionId);
        await this.connection.invoke('JoinTask', taskId);
      } catch (e) {
        console.warn('[signalr] failed to rejoin group', taskId, e);
      }
    }
  }

  private registerCallbacks(): void {
    if (!this.connection) return;

    this.connection.on('OnContentToken', (delta: string) => this.callbacks.onContentToken?.(delta));
    this.connection.on('OnThinkingToken', (delta: string) => this.callbacks.onThinkingToken?.(delta));
    this.connection.on('OnLog', (message: string) => this.callbacks.onLog?.(message));
    this.connection.on('OnScreenshot', (base64: string) => this.callbacks.onScreenshot?.(base64));
    this.connection.on('OnAgentStatus', (payload) => this.callbacks.onAgentStatus?.(payload));
    this.connection.on('OnFileCreated', (payload) => this.callbacks.onFileCreated?.(payload));
    this.connection.on('ToolAction', (event) => this.callbacks.onToolAction?.(event));
    this.connection.on('TodoUpdate', (payload) => this.callbacks.onTodoUpdate?.(payload));
    this.connection.on('OnCompleted', (payload) => this.callbacks.onCompleted?.(payload));
    this.connection.on('OnError', (error: string) => this.callbacks.onError?.(error));
    this.connection.on('OnStopped', (taskId: string) => this.callbacks.onStopped?.(taskId));
    this.connection.on('RunProjectError', (payload: RunProjectErrorPayload) => this.callbacks.onRunProjectError?.(payload));
    this.connection.on('SearchStatus', (payload: SearchStatusPayload) => this.callbacks.onSearchStatus?.(payload));
    this.connection.on('BuildProblems', (payload: BuildProblemsPayload) => this.callbacks.onProblems?.(payload));
    // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
    this.connection.on('OnPendingActionCreated', (payload: PendingActionPayload) => this.callbacks.onPendingActionCreated?.(payload));
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
    if (this.connection.state !== signalR.HubConnectionState.Connected) {
      await this.connection.start();
    }
    this.notifyState(toStatus(this.connection.state));
  }
}

export const signalrService = new SignalrService();
