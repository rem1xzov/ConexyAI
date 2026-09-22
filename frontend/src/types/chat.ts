import type { ConexyModel } from './api';
import type { BuildProblem, TaskStatus, TodoItem, ToolActionEvent } from './signalr';

export type MessageStatus = 'streaming' | 'complete' | 'error' | 'stopped';

export type ChatSessionKind = 'chat' | 'projects' | 'students';

export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  thinking?: string;
  status: MessageStatus;
  logs?: string[];
  screenshots?: string[];
  error?: string;
  currentAction?: AgentAction | null;
  toolActions?: ToolActionEvent[];
  todos?: TodoItem[];
  problems?: BuildProblem[];
  createdAt: number;
  /** The model that generated this message (used to gate voice features to flash). */
  model?: ConexyModel;
  // ATTACHMENTS_IN_BUBBLE: добавлено 2026-09-21
  /** Files sent with this message. Kept on the message so they render in its bubble and survive
   *  a reload (only a small preview is stored — the original bytes go to the model, not to disk). */
  attachments?: MessageAttachment[];
}

// ATTACHMENTS_IN_BUBBLE: добавлено 2026-09-21
export interface MessageAttachment {
  fileName: string;
  contentType: string;
  /** Downscaled data URL for display; absent when the file is not an image. */
  previewUrl?: string;
  /** Original size in bytes, when known. */
  sizeBytes?: number;
}

export interface AgentAction {
  stage: string;
  label: string;
  file?: string | null;
}

export interface ChatSession {
  id: string;
  taskId?: string;
  title: string;
  status: TaskStatus;
  model: ConexyModel;
  kind?: ChatSessionKind;
  messages: ChatMessage[];
  createdAt: number;
  isPinned?: boolean;
  // INCOGNITO_CHAT: добавлено 2026-09-20
  /** Local-only chat: hidden from the sidebar and never written to localStorage. */
  incognito?: boolean;
}
