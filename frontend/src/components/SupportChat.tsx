import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  cancelSupportTicket,
  createSupportTicket,
  escalateSupportTicket,
  returnSupportTicketToBot,
  sendSupportMessage,
} from '../api/conexyApi';
import { signalrService } from '../services/signalrService';
import type { SupportMessage, SupportTicket } from '../types/api';
import { ConfirmDialog } from './Dialog';
import { CloseIcon, SendIcon } from './Icons';

interface SupportChatProps {
  onClose: () => void;
  onToast: (message: string) => void;
}

// I18N_FORMAT: добавлено 2026-09-24 — время по языку интерфейса, а не всегда ru-RU.
function formatTime(iso: string, lang: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleTimeString(lang, { hour: '2-digit', minute: '2-digit' });
}

// SUPPORT_ECHO: добавлено 2026-09-24 (M16) — своё сообщение показывается сразу из HTTP-ответа, а
// эхо из хаба (или повтор после переподключения) с тем же id не дублирует его.
function withMessage(ticket: SupportTicket, msg: SupportMessage): SupportTicket {
  if (ticket.messages.some((m) => m.id === msg.id)) return ticket;
  return { ...ticket, messages: [...ticket.messages, msg] };
}

// SUPPORT_BOT: класс зависит от автора — своё справа, бот/оператор/система слева с разными стилями.
function messageClass(m: SupportMessage): string {
  switch (m.authorType) {
    case 'User':
      return 'support-msg support-msg--own';
    case 'Bot':
      return 'support-msg support-msg--other support-msg--bot';
    case 'Admin':
      return 'support-msg support-msg--other support-msg--admin';
    default:
      return 'support-msg support-msg--system';
  }
}

// SUPPORT: добавлено 2026-09-19
/** User-facing messenger-style support chat (AI bot first, operator escalation). */
export function SupportChat({ onClose, onToast }: SupportChatProps) {
  const { t, i18n } = useTranslation();
  const lang = i18n.resolvedLanguage ?? i18n.language;
  const [ticket, setTicket] = useState<SupportTicket | null>(null);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  // SUPPORT_BOT: идёт переход состояния (эскалация/возврат/отмена) или подтверждение.
  const [busy, setBusy] = useState(false);
  const [confirmEscalate, setConfirmEscalate] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let cancelled = false;
    let ticketId: string | null = null;

    const unsubscribe = signalrService.onSupportMessage((msg) => {
      setTicket((t) => (t && t.id === msg.ticketId ? withMessage(t, msg) : t));
    });

    async function init() {
      try {
        const t = await createSupportTicket();
        if (cancelled) return;
        ticketId = t.id;
        setTicket(t);
        await signalrService.joinSupportTicket(t.id);
      } catch {
        if (!cancelled) onToast(t('support.openError'));
      }
    }
    void init();

    return () => {
      cancelled = true;
      unsubscribe();
      if (ticketId) void signalrService.leaveSupportTicket(ticketId);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight });
  }, [ticket?.messages.length]);

  async function handleSend() {
    const content = draft.trim();
    if (!content || !ticket || sending) return;
    const ticketId = ticket.id;
    setSending(true);
    setDraft('');
    try {
      const msg = await sendSupportMessage(ticketId, content);
      // Without this the message only appeared once the hub echoed it back — and never, when the
      // echo was lost (e.g. the support group was not re-joined after a reconnect).
      if (msg && msg.id) setTicket((tk) => (tk && tk.id === ticketId ? withMessage(tk, msg) : tk));
    } catch {
      onToast(t('support.sendError'));
      // Keep what the user typed so a failed send does not lose it.
      setDraft((d) => d || content);
    } finally {
      setSending(false);
    }
  }

  // SUPPORT_BOT: общий путь для эскалации/возврата/отмены — бэкенд возвращает обновлённый тикет.
  async function transition(fn: (id: string) => Promise<SupportTicket>) {
    if (!ticket || busy) return;
    const ticketId = ticket.id;
    setBusy(true);
    try {
      const updated = await fn(ticketId);
      setTicket(updated);
    } catch {
      onToast(t('support.actionError'));
    } finally {
      setBusy(false);
    }
  }

  const status = ticket?.status ?? '';
  const showNotHelpful = !!ticket && ticket.botActive && status !== 'Closed';
  const showReturnToBot = status === 'Escalated' && !ticket?.botActive;
  const showCancel = status === 'Escalated';
  const readOnly = status === 'Closed';

  return (
    <div className="dialog-overlay" onMouseDown={onClose}>
      <div className="dialog-card support-chat" role="dialog" aria-modal="true" onMouseDown={(e) => e.stopPropagation()}>
        <div className="support-chat__head">
          <div className="dialog-title">{t('support.title')}</div>
          <button className="icon-btn" onClick={onClose} aria-label={t('common.close')} type="button">
            <CloseIcon size={18} />
          </button>
        </div>

        <div className="support-chat__messages" ref={scrollRef}>
          {!ticket ? (
            <p className="muted">{t('common.loading')}</p>
          ) : ticket.messages.length === 0 ? (
            <p className="muted support-chat__hint">{t('support.hint')}</p>
          ) : (
            ticket.messages.map((m: SupportMessage) => (
              <div key={m.id} className={messageClass(m)}>
                {m.authorType === 'Bot' && <div className="support-msg__author">{t('support.botName')}</div>}
                {m.authorType === 'Admin' && <div className="support-msg__author">{t('support.operatorName')}</div>}
                <div className="support-msg__bubble">{m.content}</div>
                {m.authorType !== 'System' && (
                  <div className="support-msg__time">{formatTime(m.createdAt, lang)}</div>
                )}
              </div>
            ))
          )}
        </div>

        {/* SUPPORT_BOT: кнопки состояния. «Не помогло» — пока бот отвечает; «Вернуться к боту» и
            «Отменить обращение» — на этапе оператора. */}
        {ticket && !readOnly && (showNotHelpful || showReturnToBot || showCancel) && (
          <div className="support-chat__actions">
            {showNotHelpful && (
              <button
                className="support-action"
                onClick={() => setConfirmEscalate(true)}
                disabled={busy}
                type="button"
              >
                {t('support.notHelpful')}
              </button>
            )}
            {showReturnToBot && (
              <button
                className="support-action"
                onClick={() => void transition(returnSupportTicketToBot)}
                disabled={busy}
                type="button"
              >
                {t('support.returnToBot')}
              </button>
            )}
            {showCancel && (
              <button
                className="support-action support-action--danger"
                onClick={() => void transition(cancelSupportTicket)}
                disabled={busy}
                type="button"
              >
                {t('support.cancelRequest')}
              </button>
            )}
          </div>
        )}

        <div className="support-chat__input">
          <input
            className="dialog-input"
            value={draft}
            placeholder={readOnly ? t('support.closedPlaceholder') : t('support.messagePlaceholder')}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.nativeEvent.isComposing) void handleSend();
            }}
            disabled={readOnly}
          />
          <button
            className="icon-btn"
            onClick={() => void handleSend()}
            disabled={!draft.trim() || sending || readOnly}
            aria-label={t('common.send')}
            type="button"
          >
            <SendIcon size={18} />
          </button>
        </div>

        {confirmEscalate && (
          <ConfirmDialog
            title={t('support.escalateConfirmTitle')}
            message={t('support.escalateConfirm')}
            confirmLabel={t('support.escalateConfirmYes')}
            onConfirm={() => {
              setConfirmEscalate(false);
              void transition(escalateSupportTicket);
            }}
            onCancel={() => setConfirmEscalate(false)}
          />
        )}
      </div>
    </div>
  );
}
