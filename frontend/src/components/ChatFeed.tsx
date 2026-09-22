import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import type { ChatSession } from '../types/chat';
import type { CommandDecisionHandler } from '../types/signalr';
import { MessageBubble } from './MessageBubble';

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
  const messagesEndRef = useRef<HTMLDivElement>(null);

  const sessionId = session?.id ?? null;
  const messageCount = session?.messages.length ?? 0;
  const lastMessage = session?.messages[messageCount - 1] ?? null;
  const lastContent = lastMessage?.content ?? '';
  const lastThinking = lastMessage?.thinking ?? '';

  const prevSessionIdRef = useRef<string | null>(null);
  const prevCountRef = useRef(0);

  function scrollToBottom(instant = false) {
    messagesEndRef.current?.scrollIntoView({ behavior: instant ? 'auto' : 'smooth', block: 'end' });
  }

  // Snap to the bottom (no animation) when switching sessions.
  useEffect(() => {
    if (prevSessionIdRef.current !== sessionId) {
      prevSessionIdRef.current = sessionId;
      prevCountRef.current = messageCount;
      scrollToBottom(true);
    }
  }, [sessionId, messageCount]);

  // Smooth scroll when a new message is appended (on Send).
  useEffect(() => {
    if (messageCount > prevCountRef.current) {
      prevCountRef.current = messageCount;
      scrollToBottom(false);
    }
  }, [messageCount]);

  // Follow the token/reasoning stream, in every mode. Instant scroll keeps the
  // feed pinned to the bottom without janky per-token animations. Skip the empty
  // assistant placeholder frame so the smooth send-scroll is not overridden.
  useEffect(() => {
    if (lastMessage?.role === 'assistant' && (lastContent.length > 0 || lastThinking.length > 0)) {
      scrollToBottom(true);
    }
  }, [lastContent, lastThinking]);

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

  return (
    <div className="feed" ref={scrollRef}>
      {session.messages.map((m, index) => (
        <MessageBubble
          key={m.id}
          message={m}
          onRegenerate={onRegenerate}
          onResend={onResend}
          onEditMessage={onEditMessage}
          onCommandDecision={onCommandDecision}
          isLast={index === session.messages.length - 1}
          onNewChat={onNewChat}
          onContinue={onContinue}
        />
      ))}
      <div ref={messagesEndRef} className="h-4 w-full shrink-0" />
    </div>
  );
}
