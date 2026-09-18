import { useState, type KeyboardEvent } from 'react';
import type { ChatMessage } from '../types/chat';
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
}

export function MessageBubble({ message, onRegenerate, onResend, onEditMessage }: MessageBubbleProps) {
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
        <div className="w-fit max-w-[85%] bg-zinc-800/90 border border-zinc-700/40 text-zinc-100 rounded-2xl px-5 py-3.5 shadow-sm">
          <div className="font-semibold text-xs text-zinc-400 mb-1">Вы</div>
          {editing ? (
            <div className="min-w-[320px]">
              <textarea
                className="w-full min-w-[320px] bg-zinc-900/70 border border-zinc-600/60 rounded-lg px-3 py-2 text-base leading-relaxed text-zinc-100 outline-none focus:border-zinc-400 resize-y"
                rows={3}
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={handleEditKeyDown}
                autoFocus
              />
              <div className="flex justify-end gap-2 mt-2">
                <button
                  className="px-2.5 py-1 rounded-md text-xs bg-zinc-700/70 hover:bg-zinc-600 text-zinc-200"
                  onClick={cancelEdit}
                >
                  Отмена
                </button>
                <button
                  className="px-2.5 py-1 rounded-md text-xs bg-indigo-600 hover:bg-indigo-500 text-white disabled:opacity-40"
                  onClick={commitEdit}
                  disabled={!draft.trim()}
                >
                  Отправить
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
          <span>Думаю…</span>
        </div>
      )}

      {showThinkingDetails && (
        <details className="thinking-details">
          <summary className="thinking-details__summary">🧠 Show thinking process</summary>
          <div className="thinking-details__body">{thinking}</div>
        </details>
      )}

      {showActionBar && currentAction && (
        <div className="flex items-center gap-2.5 px-3 py-1.5 my-2 w-fit rounded-full bg-zinc-900 border border-zinc-700/60 text-xs text-zinc-300 shadow-sm animate-pulse">
          {currentAction.stage === 'thinking' && <span className="text-purple-400">🧠</span>}
          {currentAction.stage === 'writing' && <span className="text-blue-400">📝</span>}
          {currentAction.stage === 'executing' && <span className="text-amber-400">⚡</span>}
          {currentAction.stage === 'searching' && <span className="text-sky-400">🔍</span>}
          {currentAction.stage !== 'thinking' && currentAction.stage !== 'writing' && currentAction.stage !== 'executing' && currentAction.stage !== 'searching' && (
            <span className="text-zinc-400">●</span>
          )}
          <span className="font-mono">{currentAction.label}</span>
        </div>
      )}

      <ToolActionFeed actions={toolActions} />

      <TodoPanel todos={todos} />

      <div className="w-full text-zinc-100 leading-relaxed">
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
