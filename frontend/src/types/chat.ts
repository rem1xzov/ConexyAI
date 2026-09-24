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
  // TURN_SCOPE: добавлено 2026-09-24 (H6/H7)
  /** Server task (turn) id of this assistant answer. Every turn gets its own id, so live events,
   *  the watchdog and Stop can target exactly this message — also after a page reload. */
  taskId?: string;
  /** Set while `POST /run` for this answer has not returned yet (the turn has no task id). A message
   *  still carrying it after a reload was never confirmed by the server. */
  awaitingTaskId?: boolean;
  // LOCAL_STORAGE_BUDGET: добавлено 2026-09-24 (M22)
  /** Screenshots are never written to localStorage; this counts the ones dropped from the cache. */
  screenshotsOmitted?: number;
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
  // CHAT_DELETE: добавлено 2026-09-23
  /**
   * True once this chat exists on the server (it was created by a sync, or a turn of it was
   * persisted). Only such chats may be pruned when the server no longer lists them — a local draft
   * that was never sent has no server row at all and must never be swept away.
   */
  remote?: boolean;
  // INCOGNITO_CHAT: добавлено 2026-09-20
  /** Local-only chat: hidden from the sidebar and never written to localStorage. */
  incognito?: boolean;
  // CHAT_OWNERSHIP: добавлено 2026-09-24 (C-2 403 / C-6)
  /** The server said this chat belongs to another account: nothing can be sent or joined here. */
  forbidden?: boolean;
  // LOCAL_STORAGE_BUDGET: добавлено 2026-09-24 (M22)
  /** The messages are not cached on this device (evicted to save space, or never downloaded); the
   *  transcript is loaded from the server when the chat is opened. */
  needsTranscript?: boolean;
  // TRANSCRIPT_REFRESH: добавлено 2026-09-24 (M25)
  /** The server's `lastActivityAt` / `messageCount` as of the last sync. A different value later
   *  means the chat was continued somewhere else and its transcript has to be refreshed. */
  serverActivityAt?: string;
  serverMessageCount?: number;
  /** Turns this device started since that sync: such a change on the server is our own. */
  unsyncedTurns?: number;
}
