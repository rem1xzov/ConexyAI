// PHONE_AGENT: чистая логика, на которой держится исполнение phone_* инструментов, — отдельно от
// SignalR и Capacitor, чтобы её можно было проверить без устройства.

export interface PhoneToolCall {
  callId: string;
  taskId?: string;
  tool: string;
  /** JSON-строка аргументов — в том виде, в каком её ждёт нативный плагин. */
  args: string;
}

/**
 * Разбирает событие хаба `PhoneTool`. Контракт: объект `{ callId, taskId, tool, arguments }`, где
 * `arguments` — JSON-строка. Принимаем и PascalCase (на случай другой настройки сериализации),
 * и уже распарсенный объект аргументов. Возвращает null, если ответить всё равно некуда (нет callId).
 */
export function parsePhoneToolCall(raw: unknown): PhoneToolCall | null {
  if (!raw || typeof raw !== 'object') return null;
  const record = raw as Record<string, unknown>;
  const pick = (camel: string): unknown => record[camel] ?? record[camel.charAt(0).toUpperCase() + camel.slice(1)];

  const callId = pick('callId');
  if (typeof callId !== 'string' || !callId) return null;

  const tool = pick('tool');
  const rawArgs = pick('arguments');
  let args: string;
  if (typeof rawArgs === 'string') args = rawArgs.trim() ? rawArgs : '{}';
  else if (rawArgs && typeof rawArgs === 'object') args = JSON.stringify(rawArgs);
  else args = '{}';

  const taskId = pick('taskId');
  return {
    callId,
    taskId: typeof taskId === 'string' ? taskId : undefined,
    tool: typeof tool === 'string' ? tool : '',
    args,
  };
}

/**
 * Дедупликация callId: на один вызов отвечаем ровно один раз. Хаб может доставить событие повторно
 * после переподключения, а таймаут и поздний ответ плагина не должны отправить два результата.
 * Хранит ограниченное число последних идентификаторов.
 */
export class CallDedupe {
  private readonly seen = new Set<string>();

  constructor(private readonly limit = 500) {}

  /** True — вызов новый, его надо выполнить; false — этот callId уже взят в работу. */
  claim(callId: string): boolean {
    if (this.seen.has(callId)) return false;
    this.seen.add(callId);
    if (this.seen.size > this.limit) {
      const oldest = this.seen.values().next().value;
      if (oldest !== undefined) this.seen.delete(oldest);
    }
    return true;
  }
}

/** Инструменты телефона серверного агента называются `phone_*`. */
export function isPhoneToolName(name: string | undefined): boolean {
  return typeof name === 'string' && name.startsWith('phone_');
}

export interface PhoneToolOutcome {
  output: string;
  isError: boolean;
}

/** Нормализует ответ плагина: он обязан вернуть `{ output, isError }`, но проверяем, а не верим. */
export function normalizeToolOutcome(result: unknown): PhoneToolOutcome {
  if (result && typeof result === 'object') {
    const record = result as { output?: unknown; isError?: unknown };
    const output = typeof record.output === 'string' ? record.output : record.output == null ? '' : String(record.output);
    return { output, isError: record.isError === true };
  }
  return { output: 'Плагин телефона вернул пустой ответ', isError: true };
}

export function errorToOutcome(error: unknown): PhoneToolOutcome {
  const message =
    error instanceof Error ? error.message : typeof error === 'string' ? error : (error as { message?: string } | null)?.message;
  return { output: message ? `Ошибка инструмента телефона: ${message}` : 'Ошибка инструмента телефона', isError: true };
}

/** Ждёт `promise`, но не дольше `ms`: по таймауту возвращает результат-ошибку вместо зависания. */
export function withTimeout(promise: Promise<PhoneToolOutcome>, ms: number, tool: string): Promise<PhoneToolOutcome> {
  return new Promise((resolve) => {
    const timer = setTimeout(
      () => resolve({ output: `Таймаут: инструмент ${tool || 'phone'} не ответил за ${Math.round(ms / 1000)} с`, isError: true }),
      ms,
    );
    promise.then(
      (value) => {
        clearTimeout(timer);
        resolve(value);
      },
      (error: unknown) => {
        clearTimeout(timer);
        resolve(errorToOutcome(error));
      },
    );
  });
}
