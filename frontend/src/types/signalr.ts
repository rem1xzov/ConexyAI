export interface TaskCompletedPayload {
  taskId: string;
  result: string;
}

export interface AgentStatusPayload {
  stage: string;
  label: string;
  file?: string | null;
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
  // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
  workingDirectory?: string;
  output?: string;
  pendingActionId?: string;
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
  data: string;
  /** "pty" | "agent" | "user" */
  source?: string;
}

export interface RunProjectResult {
  needsManualConfig: boolean;
  command?: string | null;
  error?: string | null;
  /** True when the run was launched in a separate visible console window (Windows dev). */
  launchedInSeparateWindow?: boolean;
}

export interface RunProjectErrorPayload {
  chatId: string;
  message: string;
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

export interface SignalrCallbacks {
  onContentToken?: (delta: string) => void;
  onThinkingToken?: (delta: string) => void;
  onLog?: (message: string) => void;
  onScreenshot?: (base64: string) => void;
  onAgentStatus?: (payload: AgentStatusPayload) => void;
  onFileCreated?: (payload: FileCreatedPayload) => void;
  onToolAction?: (event: ToolActionEvent) => void;
  onTodoUpdate?: (payload: TodoUpdatePayload) => void;
  onCompleted?: (payload: TaskCompletedPayload) => void;
  onError?: (error: string) => void;
  onStopped?: (taskId: string) => void;
  onRunProjectError?: (payload: RunProjectErrorPayload) => void;
  onSearchStatus?: (payload: SearchStatusPayload) => void;
  onProblems?: (payload: BuildProblemsPayload) => void;
  // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
  onPendingActionCreated?: (payload: PendingActionPayload) => void;
  // SUPPORT: добавлено 2026-09-19
  onSupportMessageReceived?: (payload: SupportMessagePayload) => void;
}

// SUPPORT: добавлено 2026-09-19
export interface SupportMessagePayload {
  id: string;
  ticketId: string;
  senderId: string;
  content: string;
  createdAt: string;
  isFromAdmin: boolean;
}

export type TaskStatus = 'Idle' | 'Running' | 'Completed' | 'Failed' | 'Stopped';
