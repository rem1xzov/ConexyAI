import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { ChatSession } from '../types/chat';
import type { CommandDecisionHandler } from '../types/signalr';
import { MessageBubble } from './MessageBubble';
import { ScrollToBottomButton } from './ScrollToBottomButton';

interface ChatFeedProps {
  session: ChatSession | null;
  nickname?: string | null;
  onRegenerate?: (assistantMessageId: string) => void;
  onResend?: (messageId: string) => void;
  onEditMessage?: (messageId: string, newContent: string) => void;
  // COMMAND_CONFIRM: добавлено 2026-09-20
  onCommandDecision?: CommandDecisionHandler;
  // NEW_CHAT_LOGO: добавлено 2026-09-20
  onNewChat?: () => void;
  // CONTINUE_GENERATION: добавлено 2026-09-21
  onContinue?: (messageId: string) => void;
  // AGENT_FEED_ZED: добавлено 2026-09-23
  /** Opens a file mentioned by an agent action row in the workspace editor. */
  onOpenFile?: (path: string) => void;
  // LOCAL_STORAGE_BUDGET: добавлено 2026-09-24 (M22/M25) — переписка чата грузится с сервера
  // (её нет в кеше этого устройства); без этого открытый чат выглядел бы пустым.
  loading?: boolean;
  loadFailed?: boolean;
  onRetryLoad?: () => void;
  // STREAM_FOLLOW: true in the agent tabs (Coder/Cowork), where the value is the live step and log
  // feed, so the view follows it. False in plain chats (flash/pro/students), where the answer must
  // grow without dragging the view along — see the effect below.
  followStream?: boolean;
}

export function ChatFeed({
  session,
  nickname,
  onRegenerate,
  onResend,
  onEditMessage,
  onCommandDecision,
  onNewChat,
  onContinue,
  onOpenFile,
  loading,
  loadFailed,
  onRetryLoad,
  followStream = true,
}: ChatFeedProps) {
  const { t } = useTranslation();
  const scrollRef = useRef<HTMLDivElement>(null);

  // FOLLOW_BOTTOM: добавлено 2026-09-22
  // `pinned` = «пользователь смотрит хвост ленты», то есть автоскролл разрешён. Держим его в ref
  // (а не только в state), чтобы эффект слежения читал актуальное значение без перезапуска на
  // каждом изменении флага, и чтобы программный скролл не отключал сам себя.
  const pinnedRef = useRef(true);
  const [showScrollButton, setShowScrollButton] = useState(false);

  const sessionId = session?.id ?? null;
  const messageCount = session?.messages.length ?? 0;
  const lastMessage = session?.messages[messageCount - 1] ?? null;

  const prevSessionIdRef = useRef<string | null>(null);
  // While a programmatic smooth scroll is running, its own scroll events must not be mistaken for
  // the user moving away from the bottom — otherwise clicking the button instantly re-shows it.
  const programmaticUntilRef = useRef(0);

  // One epsilon for «мы всё ещё у хвоста»: осознанного движения вверх на 10px достаточно, чтобы
  // выключить автоследование, поэтому читающего выше пользователя больше никуда не тянет.
  const MANUAL_SCROLL_EPSILON_PX = 10;
  // Кнопка «вниз» ждёт куда большего сдвига: иначе она мигала бы от любого толчка.
  const SCROLL_BUTTON_THRESHOLD_PX = 140;

  /** Instant scroll (no animation) — used when following the stream token by token. */
  const jumpToBottom = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  }, []);

  const handleScroll = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    if (Date.now() < programmaticUntilRef.current) return;
    const distance = el.scrollHeight - el.scrollTop - el.clientHeight;
    pinnedRef.current = distance <= MANUAL_SCROLL_EPSILON_PX;
    setShowScrollButton(distance > SCROLL_BUTTON_THRESHOLD_PX);
  }, []);

  // Clicking the button re-enables following and animates to the newest message.
  const scrollToBottom = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    pinnedRef.current = true;
    setShowScrollButton(false);
    programmaticUntilRef.current = Date.now() + 800;
    el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
  }, []);

  // Snap to the bottom (no animation) when switching sessions, and start following again —
  // a new conversation should never open already scrolled away from its own tail.
  useEffect(() => {
    if (prevSessionIdRef.current !== sessionId) {
      prevSessionIdRef.current = sessionId;
      pinnedRef.current = true;
      setShowScrollButton(false);
      jumpToBottom();
    }
  }, [sessionId, jumpToBottom]);

  // FOLLOW_BOTTOM: единый ключ «в ленте что-то выросло», и ключ «появился новый ход». Разделены
  // намеренно: реакция на них разная (см. эффекты ниже).
  // Ключ роста покрывает и стриминг текста, и thinking, и компактный таймлайн шагов, и карточки
  // подтверждения — раньше слежение зависело только от длины текста и thinking, поэтому новые шаги
  // агента, карточки команд и todo-план выталкивали контент вниз без прокрутки.
  const turnKey = `${messageCount}|${lastMessage?.id ?? ''}`;
  const growthKey = [
    lastMessage?.content.length ?? 0,
    lastMessage?.thinking?.length ?? 0,
    lastMessage?.toolActions?.length ?? 0,
    lastMessage?.steps?.length ?? 0,
    lastMessage?.todos?.length ?? 0,
    lastMessage?.currentAction?.label ?? '',
    lastMessage?.status ?? '',
  ].join('|');
  const prevTurnKeyRef = useRef(turnKey);

  // A new turn is a deliberate step forward: jump to it and start following again, even if the user
  // had scrolled up in the history before typing.
  useEffect(() => {
    if (prevTurnKeyRef.current === turnKey) return;
    prevTurnKeyRef.current = turnKey;
    pinnedRef.current = true;
    setShowScrollButton(false);
    jumpToBottom();
  }, [turnKey, jumpToBottom]);

  // STREAM_FOLLOW: что делать, пока растёт ответ.
  //   агентские вкладки (followStream) — держим хвост в поле зрения: там ценность в живом потоке
  //   шагов и логов;
  //   обычный чат — НЕ следуем. Экран остаётся там, куда его поставил новый ход, поэтому ответ
  //   можно читать с первой строки, пока он печатается. Рост здесь только снимает флаг «мы внизу»,
  //   из-за чего обработчик изменения размеров ниже перестаёт дёргать вид при показе клавиатуры.
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    if (followStream) {
      if (pinnedRef.current) jumpToBottom();
      return;
    }
    if (!pinnedRef.current) return;
    const distance = el.scrollHeight - el.scrollTop - el.clientHeight;
    if (distance > MANUAL_SCROLL_EPSILON_PX) {
      pinnedRef.current = false;
      setShowScrollButton(true);
    }
  }, [growthKey, followStream, jumpToBottom]);

  // Keep the pin honest when the viewport or content box changes without a scroll event
  // (window resize, mobile keyboard opening, opening the sidebar).
  useEffect(() => {
    const el = scrollRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(() => {
      if (pinnedRef.current) jumpToBottom();
    });
    observer.observe(el);
    return () => observer.disconnect();
  }, [jumpToBottom]);

  if ((loading || loadFailed) && (!session || session.messages.length === 0)) {
    return (
      <div className="feed feed--empty">
        <div className="hero">
          <p className="muted" role="status" aria-live="polite">
            {loadFailed ? t('sync.transcriptFailed') : t('sync.loadingChat')}
          </p>
          {loadFailed && onRetryLoad && (
            <button className="admin-btn" type="button" onClick={onRetryLoad}>
              {t('sync.retry')}
            </button>
          )}
        </div>
      </div>
    );
  }

  if (!session || session.messages.length === 0) {
    return (
      <div className="feed feed--empty">
        <div className="hero">
          <h1 className="hero__title">{t('chat.heroTitle', { name: nickname ?? t('chat.defaultName') })}</h1>
          <p className="hero__subtitle">{t('chat.heroSubtitle')}</p>
        </div>
      </div>
    );
  }

  // BUGFIX_LAST_USER_ACTIONS: добавлено 2026-09-22 — панель «изменить / отправить снова» имеет
  // смысл только у актуального запроса. Ищем индекс последнего сообщения пользователя: он
  // пересчитывается на каждый рендер, поэтому при новом сообщении панель у предыдущего гаснет
  // сразу, без перезагрузки.
  let lastUserIndex = -1;
  for (let i = session.messages.length - 1; i >= 0; i--) {
    if (session.messages[i].role === 'user') {
      lastUserIndex = i;
      break;
    }
  }

  return (
    // The wrapper is the positioning context for the scroll button: it must sit OUTSIDE the
    // scrollable `.feed`, otherwise the button would scroll away with the content.
    <div className="chat-feed-wrap">
      <div className="feed" ref={scrollRef} onScroll={handleScroll}>
        {session.messages.map((m, index) => (
          <MessageBubble
            key={m.id}
            message={m}
            onRegenerate={onRegenerate}
            onResend={onResend}
            onEditMessage={onEditMessage}
            onCommandDecision={onCommandDecision}
            isLast={index === session.messages.length - 1}
            isLastUserMessage={index === lastUserIndex}
            onNewChat={onNewChat}
            onContinue={onContinue}
            onOpenFile={onOpenFile}
            chatId={session.id}
            // INTERLEAVED_STREAM: only the agent tabs build a chronological feed of their turn.
            agentTurn={(session.kind ?? 'chat') === 'projects'}
          />
        ))}
      </div>

      <ScrollToBottomButton visible={showScrollButton} onClick={scrollToBottom} />
    </div>
  );
}
