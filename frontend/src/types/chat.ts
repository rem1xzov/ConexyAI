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
  // AGENT_TIMELINE: добавлено 2026-09-22
  /** Reasoning segments of the agent run, in order, each with its own start/end so the timeline
   *  shows an honest per-step duration instead of one drifted counter. */
  steps?: AgentStep[];
}

// AGENT_TIMELINE: добавлено 2026-09-22
/**
 * One reasoning step of an agent run. `afterToolCount` records how many tool actions had already
 * been emitted when the step started, which is what lets the timeline interleave reasoning rows
 * with the tool rows in their true chronological order.
 */
export interface AgentStep {
  id: string;
  stage: string;
  label: string;
  startedAt: number;
  /** Set when the next step (or the end of the run) closes this one; a closed step never ticks. */
  endedAt?: number;
  afterToolCount: number;
  // AGENT_FEED_ZED: добавлено 2026-09-23
  /** Offset into `ChatMessage.thinking` where this step's reasoning text begins. The step's own
   *  reasoning is `thinking.slice(reasoningFrom, reasoningTo)` — that is what the "Размышления"
   *  block expands to, so each step shows the text that was actually produced during it rather
   *  than repeating the backend's generic label. */
  reasoningFrom: number;
  /** End offset; absent while the step is still open (then the slice runs to the end). */
  reasoningTo?: number;
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
