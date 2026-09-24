import { memo, useMemo, useState, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import type { ChatMessage } from '../types/chat';
import type { CommandDecisionHandler } from '../types/signalr';
import { assistantPhase } from '../utils/assistantPhase';
import { artifactsToMarkdown } from '../utils/artifacts';
import { producedFiles } from '../utils/producedFiles';
import { ConexyLogo } from './ConexyLogo';
import { VisionGallery } from './VisionGallery';
import { AttachmentGrid } from './AttachmentGrid';
import { AgentTimeline } from './AgentTimeline';
import { DurationBadge } from './StepDuration';
import { TodoPanel } from './TodoPanel';
import { AssistantContent } from './artifacts/AssistantContent';
import { ProducedFiles } from './ProducedFiles';
import { MessageActions } from './MessageActions';
import { UserMessageActions } from './UserMessageActions';

interface MessageBubbleProps {
  message: ChatMessage;
  onRegenerate?: (assistantMessageId: string) => void;
  onResend?: (messageId: string) => void;
  onEditMessage?: (messageId: string, newContent: string) => void;
  // COMMAND_CONFIRM: добавлено 2026-09-20
  onCommandDecision?: CommandDecisionHandler;
  // NEW_CHAT_LOGO: добавлено 2026-09-20
  /** True for the newest message in the feed — only it carries the "new chat" mark. */
  isLast?: boolean;
  // BUGFIX_LAST_USER_ACTIONS: добавлено 2026-09-22
  /** True only for the newest user message — the edit/resend row is hidden on older prompts. */
  isLastUserMessage?: boolean;
  /** Starts a fresh conversation when the underlying reply mark is clicked. */
  onNewChat?: () => void;
  // CONTINUE_GENERATION: добавлено 2026-09-21
  /** Resumes a stopped answer from the text already on screen. */
  onContinue?: (messageId: string) => void;
  // AGENT_FEED_ZED: добавлено 2026-09-23
  /** Opens a path mentioned by an agent action row in the workspace editor. */
  onOpenFile?: (path: string) => void;
  // FILE_CARDS: добавлено 2026-09-24
  /** Chat (= workspace) id, needed to download files the agent produced. */
  chatId?: string;
}

// TURN_TIMER: добавлено 2026-09-24 (ревью L3)
// «Продолжить» оживляет то же сообщение, и его createdAt остаётся временем исходного ответа, так
// что секундомер сразу показывал «10м 3с». Момент, когда сообщение (снова) перешло в streaming,
// запоминаем здесь; карта живёт на уровне модуля, поэтому переживает переключение чатов, когда
// пузырь размонтируется и монтируется заново.
const turnStartedAt = new Map<string, number>();

function useTurnStart(messageId: string, status: ChatMessage['status'], createdAt: number): number {
  const [prevStatus, setPrevStatus] = useState(status);
  if (prevStatus !== status) {
    setPrevStatus(status);
    if (status === 'streaming') turnStartedAt.set(messageId, Date.now());
  }
  const recorded = turnStartedAt.get(messageId);
  return recorded !== undefined && recorded > createdAt ? recorded : createdAt;
}

function MessageBubbleBase({
  message,
  onRegenerate,
  onResend,
  onEditMessage,
  onCommandDecision,
  isLast = false,
  isLastUserMessage = false,
  onNewChat,
  onContinue,
  onOpenFile,
  chatId,
}: MessageBubbleProps) {
  const { t } = useTranslation();
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');

  const isUser = message.role === 'user';
  const turnStart = useTurnStart(message.id, message.status, message.createdAt);

  const content = message.content ?? '';
  const thinking = message.thinking ?? '';
  const screenshots = message.screenshots ?? [];
  const streaming = message.status === 'streaming';

  const hasThinking = thinking.length > 0;
  const hasScreenshots = screenshots.length > 0;

  const currentAction = message.currentAction ?? null;
  const toolActions = message.toolActions ?? [];
  const todos = message.todos ?? [];
  // ATTACHMENTS_IN_BUBBLE: добавлено 2026-09-21
  const attachments = message.attachments ?? [];

  // ASSISTANT_PHASES: добавлено 2026-09-20 — the message lifecycle drives what is shown:
  // a reasoning accordion and tool pills while working, then the streamed answer.
  const phase = assistantPhase(message);
  // STREAM_LOGO: the running light stays visible for the whole generation and always sits last,
  // under whatever is currently the newest content — so the growing answer pushes it down.
  const showGeneratingLogo = streaming;
  // AGENT_FEED_ZED: в агентской ленте каждый шаг размышления теперь сам раскрывает свой кусок
  // reasoning-а (ThinkingStep), поэтому отдельная аккордеон-«Процесс размышлений» наверху была бы
  // тем же текстом дважды. Оставляем её только там, где шагов нет: обычный чат, а также
  // восстановленное после перезагрузки сообщение (steps живут только в памяти вкладки).
  const hasSteps = (message.steps?.length ?? 0) > 0;
  const showThinkingAccordion = (hasThinking || phase === 'thinking') && !hasSteps;
  // Live agent status, rendered as a pill only when no tool event already covers it.
  const statusPill =
    phase === 'tool_calling' && currentAction ? { label: currentAction.label } : null;
  const showActions = message.status !== 'streaming';
  // FILE_CARDS: files the agent wrote during this turn, derived from its logs and editor events.
  const logs = message.logs;
  const files = useMemo(
    () => (isUser ? [] : producedFiles({ logs, toolActions: message.toolActions })),
    [isUser, logs, message.toolActions],
  );
  // ARTIFACTS: copy / export get ordinary fenced blocks instead of raw <conexy_artifact> markup.
  const exportContent = useMemo(() => (isUser ? content : artifactsToMarkdown(content)), [isUser, content]);

  function startEdit() {
    setDraft(content);
    setEditing(true);
  }

  function commitEdit() {
    const next = draft.trim();
    if (!next) return;
    setEditing(false);
    onEditMessage?.(message.id, next);
  }

  function cancelEdit() {
    setEditing(false);
    setDraft('');
  }

  function handleEditKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      commitEdit();
    } else if (e.key === 'Escape') {
      cancelEdit();
    }
  }

  // User message: pushed to the right edge of the centered column.
  if (isUser) {
    return (
      <div className="w-full max-w-3xl mx-auto my-4 flex flex-col items-end px-2">
        <div className="w-fit max-w-[85%] chat-surface rounded-2xl px-5 py-3.5 shadow-sm">
          <div className="font-semibold text-xs chat-muted mb-1">{t('message.you')}</div>
          {/* ATTACHMENTS_IN_BUBBLE: the files travel with this message, inside its bubble —
              images as a compact grid, anything else as a file chip. */}
          {attachments.length > 0 && !editing && <AttachmentGrid attachments={attachments} />}
          {editing ? (
            <div className="min-w-[320px]">
              <textarea
                className="w-full min-w-[320px] chat-input rounded-lg px-3 py-2 text-base leading-relaxed outline-none resize-y"
                rows={3}
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={handleEditKeyDown}
                autoFocus
              />
              <div className="flex justify-end gap-2 mt-2">
                <button
                  className="px-2.5 py-1 rounded-md text-xs chat-btn"
                  onClick={cancelEdit}
                >
                  {t('common.cancel')}
                </button>
                <button
                  className="px-2.5 py-1 rounded-md text-xs chat-btn-accent disabled:opacity-40"
                  onClick={commitEdit}
                  disabled={!draft.trim()}
                >
                  {t('common.send')}
                </button>
              </div>
            </div>
          ) : (
            <div className="whitespace-pre-wrap text-base leading-relaxed break-words">{content}</div>
          )}
        </div>
        {/* BUGFIX_LAST_USER_ACTIONS: edit/resend only make sense for the current prompt, so the
            row is rendered for the newest user message and nowhere else. */}
        {showActions && !editing && isLastUserMessage && (
          <UserMessageActions
            content={content}
            onResend={onResend ? () => onResend(message.id) : undefined}
            onEdit={startEdit}
          />
        )}
      </div>
    );
  }

  // Assistant message: centered full-width column.
  return (
    <div className="w-full max-w-3xl mx-auto my-6 px-2">
      {/* Agent task plan, kept above the working blocks so the required order
          pills -> running light -> answer text stays intact. */}
      <TodoPanel todos={todos} />

      {/* 1. Thought process: the reasoning accordion comes first. */}
      {showThinkingAccordion && (
        <details className="thinking-details">
          <summary className="thinking-details__summary">{t('message.showThinking')}</summary>
          <div className="thinking-details__body">
            {thinking || <span className="chat-muted">{t('message.thinking')}</span>}
          </div>
        </details>
      )}

      {/* 2. Compact step timeline: reasoning rows + tool rows, and a full card only for the
          commands that actually need the user's decision. */}
      <AgentTimeline
        actions={toolActions}
        steps={message.steps}
        thinking={thinking}
        onCommandDecision={onCommandDecision}
        statusPill={statusPill}
        onOpenPath={onOpenFile}
      />

      {/* 4. Streamed answer (Markdown + artifact cards in place of <conexy_artifact> tags). */}
      <div className="w-full chat-text leading-relaxed">
        <div className="break-words">
          <AssistantContent
            messageId={message.id}
            createdAt={message.createdAt}
            content={content}
            streaming={streaming}
          />
        </div>
      </div>

      {/* FILE_CARDS: добавлено 2026-09-24 — files produced in this turn, with a download button. */}
      <ProducedFiles files={files} chatId={chatId} onOpenFile={onOpenFile} />

      {/* 5. Running light: last element of the message while generating, so it starts directly
          under the timeline and is pushed down as the answer grows. This is the ONLY stopwatch in
          the feed: one honest counter for the whole turn, instead of a timer per step that reset
          to `1с` several times per answer. */}
      {showGeneratingLogo && (
        <div className="msg-generating">
          <ConexyLogo size={24} active />
          <DurationBadge startedAt={turnStart} className="thinking-timer" />
        </div>
      )}

      {message.error && <div className="msg__error">{message.error}</div>}
      {hasScreenshots && <VisionGallery screenshots={screenshots} />}

      {/* CONTINUE_GENERATION: the stop marker is derived from the status, not baked into the
          text, so the stored content stays exactly the partial answer to resume from. */}
      {message.status === 'stopped' && <div className="msg-stopped">{t('agent.generationStopped')}</div>}

      {showActions && (
        <MessageActions
          content={exportContent}
          onRegenerate={onRegenerate ? () => onRegenerate(message.id) : undefined}
          onContinue={
            message.status === 'stopped' && onContinue ? () => onContinue(message.id) : undefined
          }
        />
      )}

      {/* NEW_CHAT_LOGO: добавлено 2026-09-20 — after generation the reply ends with a static
          mark that opens a new conversation (with a hint bubble on hover / keyboard focus). */}
      {isLast && !streaming && (
        <div className="msg-newchat">
          <button
            type="button"
            className="msg-newchat__btn"
            onClick={onNewChat}
            disabled={!onNewChat}
            aria-label={t('chat.newChatTooltip')}
          >
            <ConexyLogo size={26} />
          </button>
          <span className="msg-newchat__bubble" role="tooltip">
            {t('chat.newChatTooltip')}
          </span>
        </div>
      )}
    </div>
  );
}

// BUGFIX_PERF: добавлено 2026-09-21
// Every streamed token replaces one message object in the session list, which re-renders the whole
// feed. Memoising the bubble keeps that O(1) instead of re-rendering (and re-parsing) every
// message in a long conversation — that was what made the action row feel laggy to click.
// All callbacks passed in from App are stable (useCallback), so the memo actually holds.
export const MessageBubble = memo(MessageBubbleBase);
