import { memo, useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import type { ChatMessage } from '../types/chat';
import type { CommandDecisionHandler } from '../types/signalr';
import { assistantPhase } from '../utils/assistantPhase';
import { artifactsToMarkdown } from '../utils/artifacts';
import { producedFiles } from '../utils/producedFiles';
import { copyText } from '../utils/clipboard';
import { ConexyLogo } from './ConexyLogo';
import { ThinkingStep } from './ThinkingStep';
import { VisionGallery } from './VisionGallery';
import { AttachmentGrid } from './AttachmentGrid';
import { AgentTimeline, feedSummary } from './AgentTimeline';
import { blocksFromLegacy } from '../utils/turnBlocks';
import { DurationBadge } from './StepDuration';
import { TodoPanel } from './TodoPanel';
import { AssistantContent } from './artifacts/AssistantContent';
import { ProducedFiles } from './ProducedFiles';
import { MessageActions } from './MessageActions';
// WEB_DEVICE: добавлено 2026-10-18
import { DeviceTurnExtras, DeviceQuestionCards } from './DeviceTurnExtras';
import { VariantPager } from './VariantPager';
import { CopyIcon, EditIcon, ShareIcon, TextSelectIcon } from './Icons';

interface MessageBubbleProps {
  message: ChatMessage;
  onRegenerate?: (assistantMessageId: string) => void;
  onEditMessage?: (messageId: string, newContent: string) => void;
  // COMPOSER_EDIT: добавлено 2026-10-01 — на телефоне редактирование уезжает в поле ввода
  // сообщения (как в Gemini), а не живёт inline в пузыре.
  /** Mobile: open this user message for editing inside the composer. */
  onBeginEdit?: (messageId: string, content: string) => void;
  /** Switch the visible user-message branch (no request). */
  onBranchChange?: (userMessageId: string, index: number) => void;
  /** Switch the visible assistant version (no request). */
  onVersionChange?: (assistantMessageId: string, index: number) => void;
  /** True while another message is being edited in the composer: this one fades. */
  dimmed?: boolean;
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
  // INTERLEAVED_STREAM: добавлено 2026-09-24
  /**
   * True for the agent tabs (Coder/Cowork). Only they own their text in the chronological feed; the
   * plain chat and students paths keep the answer in its own bubble, as before.
   */
  agentTurn?: boolean;
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
  onEditMessage,
  onBeginEdit,
  onBranchChange,
  onVersionChange,
  dimmed = false,
  onCommandDecision,
  isLast = false,
  isLastUserMessage = false,
  onNewChat,
  onContinue,
  onOpenFile,
  chatId,
  agentTurn = false,
}: MessageBubbleProps) {
  const { t } = useTranslation();
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  // MESSAGE_MENU: контекстное меню по долгому нажатию/правому клику на пузыре пользователя.
  const [menuOpen, setMenuOpen] = useState(false);
  const [menuCopied, setMenuCopied] = useState(false);
  const menuRef = useRef<HTMLDivElement | null>(null);
  const contentRef = useRef<HTMLDivElement | null>(null);
  const pressTimer = useRef<number | null>(null);
  const pressOrigin = useRef<{ x: number; y: number } | null>(null);

  const isUser = message.role === 'user';
  const turnStart = useTurnStart(message.id, message.status, message.createdAt);
  // MOTION: only a freshly created message animates in. Restored history must not replay the
  // entrance every time the chat is opened.
  const enterMotion = Date.now() - message.createdAt < 2500;

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
  const hasSteps = agentTurn && (message.steps?.length ?? 0) > 0;
  // INTERLEAVED_STREAM: the chronological feed, built once per event batch. A message restored from
  // a cache written before the feed existed has none, so its flat fields are replayed — which keeps
  // its tools and reasoning exactly where the old timeline drew them. Only an agent turn replays its
  // answer into the feed; everywhere else the answer stays a separate bubble (see `feed.text`).
  const feed = useMemo(
    () =>
      message.blocks ??
      blocksFromLegacy({
        toolActions: message.toolActions,
        // THINKING_ONCE: reasoning steps belong to the agent feed only. A chat turn (Flash/Pro) gets
        // ONE thinking block at the top of the answer, not a row per backend phase.
        steps: agentTurn ? message.steps : undefined,
        thinking,
        content,
        withContent: agentTurn,
      }),
    [message.blocks, message.toolActions, message.steps, thinking, content, agentTurn],
  );
  // What the feed already renders. Each of these used to be drawn separately as well — the reasoning
  // accordion, the plan and the answer bubble — which is exactly how the answer ended up glued to the
  // bottom, away from the tools it talked about.
  const parts = feedSummary(feed);
  // FLASH_NO_THINKING: Flash не рассуждает. Пауза до первого токена давала phase === 'thinking',
  // и у быстрой модели висел аккордеон «Процесс размышлений» с заглушкой «Думаю…». Теперь для
  // ходов Flash он показывается только при реально пришедших reasoning-токенах (их быть не
  // должно — модель принудительно отключена на бэкенде). Для Pro аккордеон работает как раньше.
  const modelReasons = message.model !== 'ConexyV1-flash';
  // THINKING_ONCE: one plate per answer, at the top — the model thinks, then answers. Flash never
  // reasons, so it gets no plate at all, not even the empty "thinking…" placeholder.
  const showThinkingAccordion =
    modelReasons && (hasThinking || phase === 'thinking') && !hasSteps && !parts.thoughts;
  // Live agent status, rendered as a pill only when no tool event already covers it.
  // INTERLEAVED_STREAM: offered for every phase now, not just `tool_calling`. Some phases stream no
  // text of their own — the auditor review is one — so this label is the only trace of them, and the
  // feed hides the pill whenever a thought or tool block is running anyway.
  const statusPill = currentAction ? { label: currentAction.label } : null;
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
    // COMPOSER_EDIT: правка открывается в поле ввода (как в Gemini/DeepSeek), а не inline в пузыре.
    if (onBeginEdit) {
      onBeginEdit(message.id, content);
      return;
    }
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

  // MESSAGE_MENU: вне меню или Escape — закрыть; долгое нажатие на пузыре — открыть.
  useEffect(() => {
    if (!menuOpen) return;
    function closeOnOutside(event: PointerEvent) {
      if (menuRef.current && event.target instanceof Node && !menuRef.current.contains(event.target)) {
        setMenuOpen(false);
      }
    }
    function closeOnEscape(event: globalThis.KeyboardEvent) {
      if (event.key === 'Escape') setMenuOpen(false);
    }
    document.addEventListener('pointerdown', closeOnOutside);
    document.addEventListener('keydown', closeOnEscape);
    return () => {
      document.removeEventListener('pointerdown', closeOnOutside);
      document.removeEventListener('keydown', closeOnEscape);
    };
  }, [menuOpen]);

  function cancelPress() {
    if (pressTimer.current !== null) {
      window.clearTimeout(pressTimer.current);
      pressTimer.current = null;
    }
    pressOrigin.current = null;
  }

  function beginPress(x: number, y: number) {
    cancelPress();
    pressOrigin.current = { x, y };
    pressTimer.current = window.setTimeout(() => {
      pressTimer.current = null;
      setMenuOpen(true);
      navigator.vibrate?.(15);
    }, 450);
  }

  function movePress(x: number, y: number) {
    if (pressTimer.current === null) return;
    const origin = pressOrigin.current;
    if (origin && Math.hypot(x - origin.x, y - origin.y) > 10) cancelPress();
  }

  async function menuCopy() {
    if (await copyText(content)) {
      setMenuCopied(true);
      window.setTimeout(() => setMenuCopied(false), 1200);
    }
    setMenuOpen(false);
  }

  function menuSelectText() {
    const element = contentRef.current;
    if (element) {
      const range = document.createRange();
      range.selectNodeContents(element);
      const selection = window.getSelection();
      selection?.removeAllRanges();
      selection?.addRange(range);
    }
    setMenuOpen(false);
  }

  async function menuShare() {
    setMenuOpen(false);
    try {
      if (navigator.share) await navigator.share({ text: content });
      else await copyText(content);
    } catch {
      // Пользователь отменил системный share — ничего не делаем.
    }
  }

  function menuEdit() {
    if (!isLastUserMessage) return;
    setMenuOpen(false);
    onBeginEdit?.(message.id, content);
  }

  // User message: pushed to the right edge of the centered column.
  if (isUser) {
    return (
      <div className={`w-full max-w-3xl mx-auto my-4 flex flex-col items-end px-2${enterMotion ? ' msg-enter' : ''}${dimmed ? ' msg--dimmed' : ''}`}>
        <div
          className={`user-bubble relative chat-surface rounded-2xl px-5 py-3.5 shadow-sm ${editing ? 'w-full max-w-[85%]' : 'w-fit max-w-[85%]'}`}
          onContextMenu={(event) => {
            event.preventDefault();
            setMenuOpen(true);
          }}
          onTouchStart={(event) => {
            const touch = event.touches[0];
            if (touch) beginPress(touch.clientX, touch.clientY);
          }}
          onTouchMove={(event) => {
            const touch = event.touches[0];
            if (touch) movePress(touch.clientX, touch.clientY);
          }}
          onTouchEnd={cancelPress}
          onTouchCancel={cancelPress}
        >
          <div className="font-semibold text-xs chat-muted mb-1">{t('message.you')}</div>
          {/* ATTACHMENTS_IN_BUBBLE: the files travel with this message, inside its bubble —
              images as a compact grid, anything else as a file chip. */}
          {attachments.length > 0 && !editing && <AttachmentGrid attachments={attachments} />}
          {editing ? (
            <div className="w-full min-w-0">
              <textarea
                className="w-full min-w-0 chat-input rounded-lg px-3 py-2 text-base leading-relaxed outline-none resize-y"
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
            <div ref={contentRef} className="whitespace-pre-wrap text-base leading-relaxed break-words">{content}</div>
          )}

          {menuOpen && (
            <div className="message-menu" ref={menuRef} role="menu">
              <button type="button" role="menuitem" className="message-menu__item" onClick={() => void menuCopy()}>
                <CopyIcon size={18} />
                <span>{menuCopied ? t('message.copied') : t('message.copy')}</span>
              </button>
              <button type="button" role="menuitem" className="message-menu__item" onClick={menuSelectText}>
                <TextSelectIcon size={18} />
                <span>{t('message.selectText')}</span>
              </button>
              <button
                type="button"
                role="menuitem"
                className="message-menu__item"
                onClick={menuEdit}
                disabled={!isLastUserMessage}
                title={isLastUserMessage ? undefined : t('message.editLastOnly')}
              >
                <EditIcon size={18} />
                <span>{t('message.edit')}</span>
              </button>
              <button type="button" role="menuitem" className="message-menu__item" onClick={() => void menuShare()}>
                <ShareIcon size={18} />
                <span>{t('message.share')}</span>
              </button>
            </div>
          )}
        </div>

        {message.branches && message.branches.length > 1 && (
          <VariantPager
            current={(message.activeBranch ?? 0) + 1}
            total={message.branches.length}
            onChange={(index) => onBranchChange?.(message.id, index)}
            className="variant-pager--user"
          />
        )}
      </div>
    );
  }

  // Assistant message: centered full-width column.
  return (
    <div className={`w-full max-w-3xl mx-auto my-6 px-2${enterMotion ? ' msg-enter' : ''}${dimmed ? ' msg--dimmed' : ''}`}>
      {/* Agent task plan, kept above the working blocks so the required order
          pills -> running light -> answer text stays intact. Once the plan has a block of its own in
          the feed, this would only repeat it. */}
      {!parts.plan && <TodoPanel todos={todos} />}

      {/* DEVICE_QUESTION_TOP: the agent's question (and its collapsed "your answer" line) sits at the
          top of the answer where it was asked, not buried under the whole response — Claude-style. */}
      <DeviceQuestionCards message={message} />

      {/* 1. Thought process: exactly one collapsible plate per answer, before the answer text. */}
      {showThinkingAccordion && (
        <ThinkingStep text={thinking} running={streaming && phase === 'thinking'} />
      )}

      {/* 2. Chronological feed: reasoning rows, tool rows and the model's own text in the order
          they happened — a full card only for the commands that need the user's decision. */}
      <AgentTimeline
        blocks={feed}
        messageId={message.id}
        createdAt={message.createdAt}
        streaming={streaming}
        onCommandDecision={onCommandDecision}
        statusPill={statusPill}
        onOpenPath={onOpenFile}
        agents={message.agents}
      />

      {/* 4. Answer text, for messages the feed does not carry: the plain chat and students paths. */}
      {!parts.text && (
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
      )}

      {/* FILE_CARDS: добавлено 2026-09-24 — files produced in this turn, with a download button. */}
      <ProducedFiles files={files} chatId={chatId} onOpenFile={onOpenFile} />

      {/* WEB_DEVICE: добавлено 2026-10-18 — ожидание ПК и «Изменения на ПК» под ответом.
          Вопросы агента живут выше (DeviceQuestionCards), у начала ответа. */}
      <DeviceTurnExtras message={message} />

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
      {/* M22: screenshots are not kept in local storage; say so instead of letting them vanish. */}
      {!hasScreenshots && (message.screenshotsOmitted ?? 0) > 0 && (
        <p className="message__note muted">{t('render.screenshotsOmitted', { count: message.screenshotsOmitted })}</p>
      )}

      {/* CONTINUE_GENERATION: the stop marker is derived from the status, not baked into the
          text, so the stored content stays exactly the partial answer to resume from. */}
      {message.status === 'stopped' && <div className="msg-stopped">{t('agent.generationStopped')}</div>}

      {showActions && (
        <MessageActions
          content={exportContent}
          onRegenerate={isLast && onRegenerate ? () => onRegenerate(message.id) : undefined}
          onContinue={
            message.status === 'stopped' && onContinue ? () => onContinue(message.id) : undefined
          }
        />
      )}

      {message.versions && message.versions.length > 1 && !streaming && (
        <VariantPager
          current={(message.activeVersion ?? 0) + 1}
          total={message.versions.length}
          onChange={(index) => onVersionChange?.(message.id, index)}
        />
      )}

      {/* AI_DISCLAIMER: добавлено 2026-10-01 — честная приписка под каждым ответом модели
          (чат, flash/pro, Coder, Cowork). Показываем только у завершённого ответа. */}
      {!streaming && <p className="msg-disclaimer">{t('message.disclaimer')}</p>}

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
