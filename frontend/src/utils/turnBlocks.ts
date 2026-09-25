import type { AgentStep, TurnBlock, TurnTextBlock, TurnToolBlock, TurnToolStatus } from '../types/chat';
import type { TodoItem, ToolActionEvent } from '../types/signalr';

/**
 * INTERLEAVED_STREAM: maintains an agent turn's chronological feed (see `TurnBlock`).
 *
 * Everything here is a pure function over the block list, because this is the one place where the
 * order of the feed is decided — the event handlers in `App` only call these and put the result back
 * on the message. Keeping it pure is also what makes the interleaving testable without a browser.
 */

let blockSeq = 0;

function nextId(prefix: string): string {
  blockSeq += 1;
  return `${prefix}-${blockSeq}`;
}

/**
 * The backend's `ToolActionEvent` has no tool-call id, so one invocation is identified by what it
 * does. A repeated command therefore pairs with its own newest open block rather than with an
 * earlier run of the same command (see `applyToolEvent`).
 */
export function toolPairKey(event: ToolActionEvent): string {
  return `${event.toolName}|${event.path ?? ''}|${event.command ?? ''}`;
}

export function isTerminalToolStatus(status: TurnToolStatus): boolean {
  return status === 'success' || status === 'error' || status === 'rejected';
}

function toolStatusFrom(event: ToolActionEvent): TurnToolStatus {
  switch (event.status) {
    case 'completed':
      return 'success';
    case 'failed':
      return 'error';
    case 'rejected':
      return 'rejected';
    case 'pending_confirmation':
      return 'pending_approval';
    default:
      return 'running';
  }
}

function toolBlockFrom(event: ToolActionEvent): TurnToolBlock {
  return {
    id: nextId('tool'),
    type: 'tool',
    toolCallId: toolPairKey(event),
    name: event.toolName,
    command: event.command ?? '',
    path: event.path ?? '',
    status: toolStatusFrom(event),
    output: event.output ?? undefined,
    summary: event.summary ?? undefined,
    workingDirectory: event.workingDirectory ?? undefined,
    actionId: event.pendingActionId ?? undefined,
    isDangerous: event.isDangerous ?? undefined,
  };
}

/**
 * `CommandConfirmCard` is driven by raw events, so a block that needs a decision is turned back into
 * the single event it represents.
 */
export function toolEventFromBlock(block: TurnToolBlock): ToolActionEvent {
  return {
    toolName: block.name,
    command: block.command,
    path: block.path,
    status:
      block.status === 'pending_approval'
        ? 'pending_confirmation'
        : block.status === 'success'
          ? 'completed'
          : block.status === 'error'
            ? 'failed'
            : block.status === 'rejected'
              ? 'rejected'
              : 'started',
    summary: block.summary,
    output: block.output,
    workingDirectory: block.workingDirectory,
    pendingActionId: block.actionId,
    isDangerous: block.isDangerous,
  };
}

/** Closes any thought block that is still open, so its timer stops at the moment it really ended. */
function closeThoughts(blocks: TurnBlock[], now: number): TurnBlock[] {
  return blocks.map((block) =>
    block.type === 'thought' && block.durationMs === undefined
      ? { ...block, durationMs: Math.max(0, now - block.startedAt) }
      : block,
  );
}

function thoughtBlock(content: string, startedAt: number, durationMs?: number): TurnBlock {
  return { id: nextId('thought'), type: 'thought', content, startedAt, durationMs };
}

/**
 * Answer text. It continues the last text block when one is open and starts a new one otherwise —
 * which is what puts the model's short "now I'll edit X" lines between the tools it called, instead
 * of merging them with the final answer at the bottom.
 */
export function appendTextBlock(blocks: readonly TurnBlock[], text: string, now = Date.now()): TurnBlock[] {
  if (!text) return [...blocks];
  const next = closeThoughts([...blocks], now);
  const last = next[next.length - 1];
  if (last?.type === 'text') {
    next[next.length - 1] = { ...last, content: last.content + text };
    return next;
  }
  next.push({ id: nextId('text'), type: 'text', content: text });
  return next;
}

/** Reasoning text: continues the open thought block, or opens a new one. */
export function appendThoughtBlock(blocks: readonly TurnBlock[], text: string, now = Date.now()): TurnBlock[] {
  if (!text) return [...blocks];
  const next = [...blocks];
  const last = next[next.length - 1];
  if (last?.type === 'thought') {
    next[next.length - 1] = { ...last, content: last.content + text };
    return next;
  }
  next.push(thoughtBlock(text, now));
  return next;
}

/**
 * One `ToolActionEvent`. A `started` (or a `pending_confirmation`, which an approval command sends
 * first) opens a block; every later status of the same invocation lands in that open block. An
 * outcome whose `started` twin never arrived — it fell outside the socket's window — still gets a
 * block of its own rather than being dropped.
 */
export function applyToolEvent(blocks: readonly TurnBlock[], event: ToolActionEvent, now = Date.now()): TurnBlock[] {
  const next = closeThoughts([...blocks], now);

  if (event.status === 'started') {
    next.push(toolBlockFrom(event));
    return next;
  }

  const pairKey = toolPairKey(event);
  for (let i = next.length - 1; i >= 0; i--) {
    const block = next[i];
    if (block.type !== 'tool' || block.toolCallId !== pairKey) continue;
    if (isTerminalToolStatus(block.status)) continue;
    next[i] = {
      ...block,
      status: toolStatusFrom(event),
      output: event.output ?? block.output,
      summary: event.summary ?? block.summary,
      actionId: event.pendingActionId ?? block.actionId,
      isDangerous: event.isDangerous ?? block.isDangerous,
    };
    return next;
  }

  next.push(toolBlockFrom(event));
  return next;
}

/** The plan is shown where the agent last wrote it, and updated in place when it changes. */
export function applyPlanBlock(blocks: readonly TurnBlock[], items: TodoItem[], now = Date.now()): TurnBlock[] {
  const next = closeThoughts([...blocks], now);
  const last = next[next.length - 1];
  if (last?.type === 'plan') {
    next[next.length - 1] = { ...last, items };
    return next;
  }
  next.push({ id: nextId('plan'), type: 'plan', items });
  return next;
}

/**
 * A finished turn: the server's answer is authoritative, so it replaces the text of the last text
 * block (the streamed copy can miss tokens that went by while the socket was down).
 */
export function applyFinalAnswer(blocks: readonly TurnBlock[], result: string, now = Date.now()): TurnBlock[] {
  const next = closeThoughts([...blocks], now);
  const answer = result ?? '';
  if (!answer.trim()) return next;
  for (let i = next.length - 1; i >= 0; i--) {
    const block = next[i];
    if (block.type !== 'text') continue;
    next[i] = { ...(block as TurnTextBlock), content: answer };
    return next;
  }
  next.push({ id: nextId('text'), type: 'text', content: answer });
  return next;
}

/**
 * The feed at the end of a turn: text still sitting in the frame buffer is appended first, then a
 * finished turn's `result` replaces the streamed answer (the stream can miss tokens that went by
 * while the socket was down).
 */
export function closeFeedBlocks(blocks: readonly TurnBlock[], buffered: string, result?: string): TurnBlock[] {
  const withTail = buffered ? appendTextBlock(blocks, buffered) : [...blocks];
  return result === undefined ? withTail : applyFinalAnswer(withTail, result);
}

/**
 * Replays a message that only has the flat fields — a turn cached before the feed existed, or one
 * whose stream predates a client update. Reasoning steps are placed by `afterToolCount`, exactly the
 * way the old timeline interleaved them, and the answer text goes last because that is all the flat
 * model can tell us.
 *
 * Note: a step that was still open when the cache was written has no end, so the duration it gets
 * when the next tool closes it is measured from the wall clock, not from the run. It only shows up
 * for a turn that was streaming across a reload.
 */
export function blocksFromLegacy(input: {
  toolActions?: ToolActionEvent[];
  steps?: AgentStep[];
  thinking?: string;
  content?: string;
  /**
   * Append the flat answer as a text block. True for an agent turn (which owns its text), false for
   * a message whose text is still rendered separately — otherwise it would appear twice.
   */
  withContent?: boolean;
}): TurnBlock[] {
  const toolActions = input.toolActions ?? [];
  const thinking = input.thinking ?? '';
  const ordered = [...(input.steps ?? [])].sort((a, b) => a.startedAt - b.startedAt);

  let blocks: TurnBlock[] = [];
  let cursor = 0;

  const insertStepsBefore = (toolCount: number) => {
    while (cursor < ordered.length && ordered[cursor].afterToolCount <= toolCount) {
      const step = ordered[cursor];
      cursor += 1;
      const slice = thinking.slice(step.reasoningFrom ?? 0, step.reasoningTo);
      const startedAt = step.startedAt;
      blocks.push(
        thoughtBlock(
          slice.trim() || step.label,
          startedAt,
          step.endedAt !== undefined ? Math.max(0, step.endedAt - startedAt) : undefined,
        ),
      );
    }
  };

  toolActions.forEach((event, index) => {
    insertStepsBefore(index);
    blocks = applyToolEvent(blocks, event);
  });
  insertStepsBefore(toolActions.length);

  const content = input.content ?? '';
  if ((input.withContent ?? true) && content.trim()) blocks = appendTextBlock(blocks, content);

  return blocks;
}
