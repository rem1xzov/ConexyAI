import { createContext, useContext, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { ChatMessage, DeviceQuestion } from '../types/chat';
import type { DeviceDiffFile, DeviceDiffPayload } from '../types/signalr';
import { DEVICE_DIFF_HEADING, diffStats } from '../utils/deviceTarget';
import { HighlightedCode } from './markdown/HighlightedCode';
import { ChevronDownIcon, CheckIcon, CloseIcon, SendIcon } from './Icons';

// WEB_DEVICE: добавлено 2026-10-18 — части ответа агента, работающего на ПК пользователя:
// ожидание подключения ПК, вопросы агента и изменённые на ПК файлы.

export interface DeviceTurnActions {
  runInSandbox: (taskId: string) => Promise<void>;
  cancelQueued: (taskId: string) => Promise<void>;
  answerQuestion: (messageId: string, requestId: string, answer: string) => Promise<void>;
}

export const DeviceTurnContext = createContext<DeviceTurnActions | null>(null);

export function DeviceTurnExtras({ message }: { message: ChatMessage }) {
  const actions = useContext(DeviceTurnContext);
  if (message.role !== 'assistant') return null;
  const streaming = message.status === 'streaming';
  // После перезагрузки сервер хранит diff прямо в тексте ответа — живой блок его не дублирует.
  const diffPersisted = message.content.includes(DEVICE_DIFF_HEADING);

  return (
    <>
      {streaming && message.waitingDevice && message.taskId && actions && (
        <WaitingDeviceCard taskId={message.taskId} actions={actions} />
      )}
      {(message.deviceQuestions ?? []).map((q) => (
        <DeviceQuestionCard
          key={q.requestId}
          question={q}
          // Only a missing context disables the card. Tying this to `streaming` left a question asked
          // by the PC agent after the chat turn had already finished permanently unclickable.
          disabled={!actions}
          onAnswer={(answer) => actions?.answerQuestion(message.id, q.requestId, answer) ?? Promise.resolve()}
        />
      ))}
      {message.deviceDiff && message.deviceDiff.files.length > 0 && !diffPersisted && (
        <DeviceDiffBlock diff={message.deviceDiff} />
      )}
    </>
  );
}

function WaitingDeviceCard({ taskId, actions }: { taskId: string; actions: DeviceTurnActions }) {
  const { t } = useTranslation();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);

  async function run(action: (taskId: string) => Promise<void>) {
    setBusy(true);
    setError(false);
    try {
      await action(taskId);
    } catch {
      setError(true);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="device-card device-card--waiting" role="status">
      <div className="device-card__head">
        <span className="device-target__dot" aria-hidden="true" />
        <span>{t('device.waiting')}</span>
      </div>
      <p className="device-card__hint">{t('device.waitingHint')}</p>
      <div className="device-card__actions">
        <button type="button" className="cmd-confirm__btn" disabled={busy} onClick={() => void run(actions.runInSandbox)}>
          {t('device.runInSandbox')}
        </button>
        <button type="button" className="cmd-confirm__btn cmd-confirm__btn--deny" disabled={busy} onClick={() => void run(actions.cancelQueued)}>
          {t('common.cancel')}
        </button>
        {error && <span className="chat-danger">{t('device.actionFailed')}</span>}
      </div>
    </div>
  );
}

function DeviceQuestionCard({
  question,
  disabled,
  onAnswer,
}: {
  question: DeviceQuestion;
  disabled: boolean;
  onAnswer: (answer: string) => Promise<void>;
}) {
  const { t } = useTranslation();
  const [text, setText] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState(false);
  // QUESTION_COLLAPSE: после ответа плашка сворачивается в одну строку — вопрос уже не "висит" в глазах,
  // а ответ всегда видно и можно развернуть обратно одним нажатием. Крестик сворачивает карточку
  // и до ответа, чтобы она не занимала место, но вопрос можно было раскрыть обратно.
  const [collapsed, setCollapsed] = useState(false);
  const answered = question.answer !== undefined;
  const locked = answered || sending || disabled;

  // A question becomes answered from the outside (SignalR ack or a reload), so collapse on the transition.
  useEffect(() => {
    if (answered) setCollapsed(true);
  }, [answered]);

  async function send(answer: string) {
    if (!answer.trim() || locked) return;
    setSending(true);
    setError(false);
    try {
      await onAnswer(answer.trim());
    } catch {
      setError(true);
    } finally {
      setSending(false);
    }
  }

  return (
    <div
      className={`device-card device-card--question ${answered ? 'device-card--done' : ''}${collapsed ? ' device-card--collapsed' : ''}`}
      data-question-id={question.requestId}
    >
      {collapsed ? (
        <button
          type="button"
          className="device-card__summary"
          onClick={() => setCollapsed(false)}
          aria-expanded={false}
          title={t('device.question')}
        >
          {answered ? (
            <CheckIcon size={14} className="device-card__summary-icon" />
          ) : (
            <span className="device-card__summary-dot" aria-hidden="true" />
          )}
          <span className="device-card__summary-text">
            {answered ? (
              <>
                {t('device.answered')}: <strong>{question.answer}</strong>
              </>
            ) : (
              question.question
            )}
          </span>
          <ChevronDownIcon size={14} className="device-card__summary-chev" />
        </button>
      ) : (
        <>
          <div className="device-card__head">
            <span className="device-card__label">{t('device.question')}</span>
            <button
              type="button"
              className="device-card__close"
              onClick={() => setCollapsed(true)}
              aria-label={t('common.close')}
            >
              {answered ? (
                <ChevronDownIcon size={15} className="device-card__summary-chev--up" />
              ) : (
                <CloseIcon size={15} />
              )}
            </button>
          </div>
          <p className="device-card__question">{question.question}</p>
          {question.options.length > 0 && (
            <ol className="device-answers">
              {question.options.map((option, index) => {
                const chosen = question.answer === option;
                return (
                  <li key={option}>
                    <button
                      type="button"
                      className={`device-answers__row${chosen ? ' device-answers__row--chosen' : ''}`}
                      disabled={locked}
                      onClick={() => void send(option)}
                    >
                      <span className="device-answers__num">{index + 1}</span>
                      <span className="device-answers__text">{option}</span>
                      {chosen && <CheckIcon size={15} className="device-answers__check" />}
                    </button>
                  </li>
                );
              })}
            </ol>
          )}
          {answered ? (
            <p className="device-card__hint">
              {t('device.answered')}: <strong>{question.answer}</strong>
            </p>
          ) : (
            <form
              className="device-card__form device-card__form--reply"
              onSubmit={(e) => {
                e.preventDefault();
                void send(text);
              }}
            >
              <input
                className="device-card__input"
                value={text}
                onChange={(e) => setText(e.target.value)}
                placeholder={t('device.answerPlaceholder')}
                disabled={locked}
                aria-label={t('device.answerPlaceholder')}
              />
              <button
                type="submit"
                className="device-card__send"
                disabled={locked || !text.trim()}
                aria-label={t('device.answer')}
              >
                <SendIcon size={16} />
              </button>
            </form>
          )}
          {error && <span className="chat-danger">{t('device.actionFailed')}</span>}
        </>
      )}
    </div>
  );
}

function DeviceDiffBlock({ diff }: { diff: DeviceDiffPayload }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(true);
  return (
    <div className="device-diff">
      <button type="button" className="device-diff__toggle" onClick={() => setOpen((o) => !o)} aria-expanded={open}>
        <ChevronDownIcon size={12} className={`device-diff__chevron ${open ? 'device-diff__chevron--open' : ''}`} />
        {t('device.changesTitle')}
        <span className="device-diff__count">{diff.files.length}</span>
      </button>
      {open && (
        <ul className="device-diff__files">
          {diff.files.map((file) => (
            <DeviceDiffFileRow key={file.path} file={file} />
          ))}
          {diff.truncated && <li className="device-card__hint">{t('device.changesTruncated')}</li>}
        </ul>
      )}
    </div>
  );
}

function DeviceDiffFileRow({ file }: { file: DeviceDiffFile }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const { added, removed } = diffStats(file.diff ?? '');
  return (
    <li className="device-diff__file">
      <button type="button" className="device-diff__file-head" onClick={() => setOpen((o) => !o)} aria-expanded={open}>
        <ChevronDownIcon size={11} className={`device-diff__chevron ${open ? 'device-diff__chevron--open' : ''}`} />
        <span className={`device-diff__badge device-diff__badge--${file.status}`}>
          {t(`device.status_${file.status}`)}
        </span>
        <span className="device-diff__path" title={file.path}>
          {file.path}
        </span>
        <span className="device-diff__stats">
          <span className="device-diff__added">+{added}</span> <span className="device-diff__removed">−{removed}</span>
        </span>
      </button>
      {open && file.diff && (
        <pre className="device-diff__code">
          <HighlightedCode code={file.diff} language="diff" />
        </pre>
      )}
    </li>
  );
}
