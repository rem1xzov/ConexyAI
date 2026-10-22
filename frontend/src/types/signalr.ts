export interface TaskCompletedPayload {
  taskId: string;
  result: string;
}

export interface AgentStatusPayload {
  stage: string;
  label: string;
  file?: string | null;
  // ORCHESTRA: чей это статус ("lead" или роль помощника).
  agentId?: string | null;
  agentRole?: string | null;
}

// ORCHESTRA: состав команды агентов на один ход (событие AgentTeam).
export interface AgentTeamMember {
  id: string;
  role: string;
  brief: string;
  scope: string[];
}

export interface AgentTeamPayload {
  taskId: string;
  agents: AgentTeamMember[];
}

export interface FileCreatedPayload {
  path: string;
  name: string;
}

export interface ToolActionEvent {
  toolName: string; // "str_replace_editor" | "bash" | "user"
  command: string;
  path: string; // empty for bash
  status: 'started' | 'completed' | 'failed' | 'pending_confirmation' | 'rejected';
  summary?: string;
  // BASH_DESCRIPTION: что делает команда простыми словами (модель обязана это заполнить).
  description?: string;
  // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
  workingDirectory?: string;
  output?: string;
  pendingActionId?: string;
  // COMMAND_CONFIRM: добавлено 2026-09-20
  /** Flagged by the backend classifier — styled with an accent, still confirmed like any command. */
  isDangerous?: boolean;
  // ORCHESTRA: чей это шаг ("lead" или роль помощника).
  agentId?: string;
  agentRole?: string;
}

// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
export interface PendingActionPayload {
  actionId: string;
  chatId: string;
  taskId: string;
  command: string;
  workingDirectory: string;
  status: string;
  createdAt: string;
  // BASH_DESCRIPTION: человеческое описание команды (устройство присылает его вместе с подтверждением).
  description?: string;
  // COMMAND_CONFIRM: добавлено 2026-09-20
  isDangerous?: boolean;
  // ORCHESTRA: чей агент просит подтверждение (решается по каждому агенту отдельно).
  agentId?: string;
  agentRole?: string;
}

export interface TodoItem {
  id: string;
  content: string;
  status: 'pending' | 'in_progress' | 'completed' | 'skipped';
  skip_reason?: string;
}

export interface TodoUpdatePayload {
  todos: TodoItem[];
}

export interface TerminalOutputPayload {
  sessionId: string;
  /** MULTI_TERMINAL: which terminal of the chat produced this chunk (absent = "main"). */
  terminalId?: string;
  data: string;
  /** "pty" | "agent" | "user" */
  source?: string;
}

export interface SearchStatusPayload {
  /** "searching" | "completed" */
  status: string;
  query?: string | null;
}

export interface BuildProblem {
  file: string;
  line: number;
  column: number;
  severity: 'error' | 'warning';
  code: string;
  message: string;
}

export interface BuildProblemsPayload {
  problems: BuildProblem[];
}

// COMMAND_CONFIRM: добавлено 2026-09-20
/**
 * Resolves a pending agent command. Command confirmation is rendered inside the agent timeline
 * instead of as a blocking modal, so the card needs a way back to the App-level handler; it is
 * passed down as a single callback. `allowAll` also auto-approves the rest of this task.
 *
 * COMMAND_FEEDBACK: добавлено 2026-09-22 — возвращает `false`, когда решение некому доставить
 * (гейт уже снят), чтобы карточка показала ошибку, а не молча осталась «ожидающей».
 */
export type CommandDecisionHandler = (
  actionId: string,
  approved: boolean,
  allowAll: boolean,
) => Promise<boolean>;

// STREAM_SCOPE: добавлено 2026-09-24 — контракт C-1. Каждое событие группы `task_{id}` несёт
// последним аргументом `scopeId` — id этой группы: taskId хода (события хода) или chatId (события
// рабочей области). Клиент маршрутизирует по нему и отбрасывает то, что не может сопоставить.
// Аргумент необязателен только ради совместимости со старым бэкендом.
export interface SignalrCallbacks {
  onContentToken?: (delta: string, scopeId?: string) => void;
  onThinkingToken?: (delta: string, scopeId?: string) => void;
  onLog?: (message: string, scopeId?: string) => void;
  onScreenshot?: (base64: string, scopeId?: string) => void;
  onAgentStatus?: (payload: AgentStatusPayload, scopeId?: string) => void;
  // ORCHESTRA: состав команды агентов (пришёл один раз в начале работы оркестра).
  onAgentTeam?: (payload: AgentTeamPayload, scopeId?: string) => void;
  onFileCreated?: (payload: FileCreatedPayload, scopeId?: string) => void;
  onToolAction?: (event: ToolActionEvent, scopeId?: string) => void;
  onTodoUpdate?: (payload: TodoUpdatePayload, scopeId?: string) => void;
  onCompleted?: (payload: TaskCompletedPayload, scopeId?: string) => void;
  onError?: (error: string, scopeId?: string) => void;
  onStopped?: (taskId: string, scopeId?: string) => void;
  onSearchStatus?: (payload: SearchStatusPayload, scopeId?: string) => void;
  onProblems?: (payload: BuildProblemsPayload, scopeId?: string) => void;
  // SIGNALR_RESILIENCE: добавлено 2026-09-22 — вызывается после успешного автоматического
  // переподключения, чтобы приложение сверило ход выполнения с сервером (события, которые
  // пришли пока сокет лежал, уже потеряны).
  onReconnected?: () => void;
  // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
  onPendingActionCreated?: (payload: PendingActionPayload, scopeId?: string) => void;
  // SUPPORT: добавлено 2026-09-19
  onSupportMessageReceived?: (payload: SupportMessagePayload) => void;
  // CHAT_OWNERSHIP: добавлено 2026-09-24 — контракт C-6: сервер отказал в JoinTask(id), потому что
  // задача или чат принадлежат другому аккаунту. Повторять такой вход бессмысленно.
  onJoinForbidden?: (id: string) => void;
  // WEB_DEVICE: добавлено 2026-10-18
  onDeviceQuestion?: (payload: DeviceQuestionPayload, scopeId?: string) => void;
  onDeviceDiff?: (payload: DeviceDiffPayload, scopeId?: string) => void;
}

// WEB_DEVICE: добавлено 2026-10-18 — вопрос агента, заданный с ПК, отвечается из веба.
export interface DeviceQuestionPayload {
  requestId: string;
  taskId: string;
  question: string;
  options: string[];
}

export type DeviceDiffFileStatus = 'added' | 'modified' | 'deleted';

export interface DeviceDiffFile {
  path: string;
  status: DeviceDiffFileStatus;
  diff: string;
}

/** Files the agent changed on the user's PC during the turn. */
export interface DeviceDiffPayload {
  taskId: string;
  files: DeviceDiffFile[];
  truncated: boolean;
}

// STOP_CONFIRM: добавлено 2026-09-24 — контракт C-5, ответ хаба на StopGeneration(taskId).
/**
 * - `stopping`  — a running turn is being cancelled; `OnStopped` follows.
 * - `cancelled` — the turn was still queued and will never run.
 * - `not_found` — nothing in flight for that id (already finished or unknown).
 */
export type StopGenerationResult = 'stopping' | 'cancelled' | 'not_found';

// SUPPORT: добавлено 2026-09-19
// SUPPORT_BOT: authorType различает бота, оператора, систему и пользователя.
export interface SupportMessagePayload {
  id: string;
  ticketId: string;
  senderId: string | null;
  content: string;
  createdAt: string;
  authorType: 'User' | 'Bot' | 'Admin' | 'System';
  isFromAdmin: boolean;
}

export type TaskStatus = 'Idle' | 'Running' | 'Completed' | 'Failed' | 'Stopped';
