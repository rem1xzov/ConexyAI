import { useState, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import type { ChatMessage } from '../types/chat';
import type { CommandDecisionHandler } from '../types/signalr';
import { VisionGallery } from './VisionGallery';
import { ToolActionFeed } from './ToolActionFeed';
import { TodoPanel } from './TodoPanel';
import { Markdown } from './Markdown';
import { MessageActions } from './MessageActions';
import { UserMessageActions } from './UserMessageActions';

interface MessageBubbleProps {
  message: ChatMessage;
  onRegenerate?: (assistantMessageId: string) => void;
  onResend?: (messageId: string) => void;
  onEditMessage?: (messageId: string, newContent: string) => void;
  // COMMAND_CONFIRM: добавлено 2026-09-20
  onCommandDecision?: CommandDecisionHandler;
}

export function MessageBubble({ message, onRegenerate, onResend, onEditMessage, onCommandDecision }: MessageBubbleProps) {
  const { t } = useTranslation();
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');

  const isUser = message.role === 'user';

  const content = message.content ?? '';
  const thinking = message.thinking ?? '';
  const screenshots = message.screenshots ?? [];
  const streaming = message.status === 'streaming';

  const hasThinking = thinking.length > 0;
  const hasScreenshots = screenshots.length > 0;

  const currentAction = message.currentAction ?? null;
  const showActionBar = streaming && currentAction != null && currentAction.stage !== 'idle';
  const toolActions = message.toolActions ?? [];
  const todos = message.todos ?? [];

  // While the assistant is streaming and no answer token has arrived yet, show a
  // compact "thinking" indicator. The full chain-of-thought is never rendered in the
  // main flow: it only becomes reachable (collapsed by default) once the answer starts
  // or the stream finishes — regardless of whether reasoning deltas arrive smoothly or
  // buffered in one block.
  const showThinkingBadge = streaming && content.length === 0 && !showActionBar;
  const showThinkingDetails = hasThinking && (content.length > 0 || !streaming);
  const showContentCursor = streaming && content.length > 0;
  const showActions = message.status !== 'streaming';

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
        {showActions && !editing && (
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
      {showThinkingBadge && (
        <div className="thinking-live">
          <span className="thinking-live__dot" />
          <span>{t('message.thinking')}</span>
        </div>
      )}

      {showThinkingDetails && (
        <details className="thinking-details">
          <summary className="thinking-details__summary">{t('message.showThinking')}</summary>
          <div className="thinking-details__body">{thinking}</div>
        </details>
      )}

      {showActionBar && currentAction && (
        <div className="flex items-center gap-2.5 px-3 py-1.5 my-2 w-fit rounded-full chat-surface text-xs shadow-sm animate-pulse">
          {currentAction.stage === 'thinking' && <span>🧠</span>}
          {currentAction.stage === 'writing' && <span>📝</span>}
          {currentAction.stage === 'executing' && <span>⚡</span>}
          {currentAction.stage === 'searching' && <span>🔍</span>}
          {currentAction.stage !== 'thinking' && currentAction.stage !== 'writing' && currentAction.stage !== 'executing' && currentAction.stage !== 'searching' && (
            <span className="chat-muted">●</span>
          )}
          <span className="font-mono">{currentAction.label}</span>
        </div>
      )}

      <ToolActionFeed actions={toolActions} onCommandDecision={onCommandDecision} />

      <TodoPanel todos={todos} />

      <div className="w-full chat-text leading-relaxed">
        <div className="break-words">
          <Markdown text={content} />
          {showContentCursor && <span className="cursor" />}
        </div>
      </div>

      {message.error && <div className="msg__error">{message.error}</div>}
      {hasScreenshots && <VisionGallery screenshots={screenshots} />}

      {showActions && (
        <MessageActions
          content={content}
          onRegenerate={onRegenerate ? () => onRegenerate(message.id) : undefined}
        />
      )}
    </div>
  );
}
