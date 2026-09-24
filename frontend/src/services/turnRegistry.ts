// TURN_SCOPE: добавлено 2026-09-24 (H6/H7)
// Раньше в App был ОДИН глобальный `streamingRef`: любой токен, пришедший по SignalR, дописывался
// в «текущее» сообщение. Enter во время генерации или отправка в другом чате перенаправляли хвост
// ответа A в пузырь B, а OnCompleted закрывал не тот ход (рецидив QA §1/§2). Теперь каждый ход —
// отдельный контекст, ключом служит его taskId (у каждого хода свой, контракт C-1), а события
// маршрутизируются по scopeId.

/** One answer being generated. */
export interface TurnContext {
  /** Chat (session) id — also the scope of the chat's workspace events. */
  sessionId: string;
  /** The assistant message this turn writes into. */
  messageId: string;
  /** Server task id; absent while `POST /run` is still in flight. */
  taskId?: string;
  /** The user pressed Stop before the server returned a task id. */
  stopRequested?: boolean;
  /** Picked up after a reload/reconnect rather than started by this page. */
  adopted?: boolean;
  startedAt: number;
}

function norm(id: string): string {
  return id.toLowerCase();
}

/** At most one turn per chat; turns are found by task id (turn events) or chat id (workspace events). */
export class TurnRegistry {
  private readonly byTaskId = new Map<string, TurnContext>();
  private readonly bySessionId = new Map<string, TurnContext>();

  /** Registers a turn (with or without a task id). Replaces any stale turn of the same chat. */
  begin(ctx: TurnContext): void {
    const previous = this.bySessionId.get(norm(ctx.sessionId));
    if (previous && previous !== ctx) this.end(previous);
    this.bySessionId.set(norm(ctx.sessionId), ctx);
    if (ctx.taskId) this.byTaskId.set(norm(ctx.taskId), ctx);
  }

  /** The server accepted the turn: from now on its events are routed by this task id. */
  attachTask(ctx: TurnContext, taskId: string): void {
    ctx.taskId = taskId;
    if (this.bySessionId.get(norm(ctx.sessionId)) === ctx) {
      this.byTaskId.set(norm(taskId), ctx);
    }
  }

  byTask(taskId: string | null | undefined): TurnContext | undefined {
    return taskId ? this.byTaskId.get(norm(taskId)) : undefined;
  }

  bySession(sessionId: string | null | undefined): TurnContext | undefined {
    return sessionId ? this.bySessionId.get(norm(sessionId)) : undefined;
  }

  /** True while `ctx` is still the live turn of its chat. */
  isActive(ctx: TurnContext): boolean {
    return this.bySessionId.get(norm(ctx.sessionId)) === ctx;
  }

  /** Forgets a turn (only if it is still the registered one). */
  end(ctx: TurnContext | undefined): void {
    if (!ctx) return;
    if (this.bySessionId.get(norm(ctx.sessionId)) === ctx) this.bySessionId.delete(norm(ctx.sessionId));
    if (ctx.taskId && this.byTaskId.get(norm(ctx.taskId)) === ctx) this.byTaskId.delete(norm(ctx.taskId));
  }

  all(): TurnContext[] {
    return [...this.bySessionId.values()];
  }

  get size(): number {
    return this.bySessionId.size;
  }

  clear(): void {
    this.byTaskId.clear();
    this.bySessionId.clear();
  }
}
