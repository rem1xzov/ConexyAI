import type { ChatMessage } from '../types/chat';

// ASSISTANT_PHASES: добавлено 2026-09-20
/**
 * Lifecycle phase of an assistant message, mirroring the Claude client:
 *
 *   thinking     -> the model is producing reasoning tokens
 *   tool_calling -> the agent is running a tool (RAG search, sandboxed bash, web search, ...)
 *   streaming    -> the answer text is arriving
 *   completed    -> generation finished (also covers error / stop)
 *
 * The phase is derived, never stored: it reads only signals the stream already provides, so
 * there is no extra state to keep in sync.
 */
export type AssistantPhase = 'thinking' | 'tool_calling' | 'streaming' | 'completed';

/** Agent stages that mean "a tool is running right now". */
const TOOL_STAGES = new Set(['searching', 'executing', 'writing']);

export function assistantPhase(message: ChatMessage): AssistantPhase {
  if (message.status !== 'streaming') return 'completed';

  // The first answer token ends both reasoning and tool work: the text takes over.
  if ((message.content ?? '').length > 0) return 'streaming';

  const lastToolAction = (message.toolActions ?? []).at(-1);
  if (lastToolAction?.status === 'started' || lastToolAction?.status === 'pending_confirmation') {
    return 'tool_calling';
  }

  if (TOOL_STAGES.has(message.currentAction?.stage ?? '')) return 'tool_calling';

  return 'thinking';
}

/** True while the running light should be visible inside the message flow. */
export function isWorkingPhase(phase: AssistantPhase): boolean {
  return phase === 'thinking' || phase === 'tool_calling';
}
