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

  // Один порог на оба решения: он и «мы внизу» (для автоследования), и «показать кнопку».
  // 140px — заметное расстояние: микроскроллы на пару пикселей и рост контента не включают
  // кнопку, но осознанный уход вверх по истории — включает.
  const PIN_THRESHOLD_PX = 140;

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
    const pinned = distance <= PIN_THRESHOLD_PX;
    pinnedRef.current = pinned;
    setShowScrollButton(!pinned);
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

  // FOLLOW_BOTTOM: единый ключ «в ленте что-то выросло». Раньше слежение зависело только от
  // длины текста и thinking, поэтому новые шаги агента, карточки команд и todo-план выталкивали
  // контент вниз без прокрутки — именно это и выглядело как «лента не следует за агентом».
  // Ключом покрыт и стриминг текста, и компактный таймлайн шагов, и карточки подтверждения.
  const followKey = [
    messageCount,
    lastMessage?.id ?? '',
    lastMessage?.status ?? '',
    lastMessage?.content.length ?? 0,
    lastMessage?.thinking?.length ?? 0,
    lastMessage?.toolActions?.length ?? 0,
    lastMessage?.steps?.length ?? 0,
    lastMessage?.todos?.length ?? 0,
    lastMessage?.currentAction?.label ?? '',
  ].join('|');

  useEffect(() => {
    // Only follow while the user is (or was) at the bottom — never yank them back mid-reading.
    if (!pinnedRef.current) return;
    jumpToBottom();
  }, [followKey, jumpToBottom]);

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
          />
        ))}
      </div>

      <ScrollToBottomButton visible={showScrollButton} onClick={scrollToBottom} />
    </div>
  );
}
