import { useState, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import type { ChatMessage } from '../types/chat';
import type { CommandDecisionHandler } from '../types/signalr';
import { assistantPhase, isWorkingPhase } from '../utils/assistantPhase';
import { ConexyLogo } from './ConexyLogo';
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
  const toolActions = message.toolActions ?? [];
  const todos = message.todos ?? [];

  // ASSISTANT_PHASES: добавлено 2026-09-20 — the message lifecycle drives what is shown:
  // a reasoning accordion and tool pills while working, then the streamed answer.
  const phase = assistantPhase(message);
  // The running light lives in the message flow and disappears the moment the answer starts.
  const showGeneratingLogo = streaming;
  const logoAnimating = isWorkingPhase(phase);
  const showThinkingAccordion = hasThinking || phase === 'thinking';
  // Live agent status, rendered as a pill only when no tool event already covers it.
  const statusPill =
    phase === 'tool_calling' && currentAction ? { label: currentAction.label } : null;
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

      {/* 2. Tool execution pills (running -> expandable result). */}
      <ToolActionFeed
        actions={toolActions}
        onCommandDecision={onCommandDecision}
        statusPill={statusPill}
      />

      {/* 3. Running light: only while a phase is actually working, hidden as soon as the
          first answer token arrives and the text takes its place. */}
      {showGeneratingLogo && (
        <div className={`msg-generating ${logoAnimating ? '' : 'msg-generating--done'}`}>
          <ConexyLogo size={30} active={logoAnimating} />
        </div>
      )}

      {/* 4. Streamed answer. */}

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
