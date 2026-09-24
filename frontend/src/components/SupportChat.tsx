import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { createSupportTicket, sendSupportMessage } from '../api/conexyApi';
import { signalrService } from '../services/signalrService';
import type { SupportMessage, SupportTicket } from '../types/api';
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

// SUPPORT: добавлено 2026-09-19
/** User-facing messenger-style support chat. */
export function SupportChat({ onClose, onToast }: SupportChatProps) {
  const { t, i18n } = useTranslation();
  const lang = i18n.resolvedLanguage ?? i18n.language;
  const [ticket, setTicket] = useState<SupportTicket | null>(null);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
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
              <div key={m.id} className={`support-msg ${m.isFromAdmin ? 'support-msg--other' : 'support-msg--own'}`}>
                <div className="support-msg__bubble">{m.content}</div>
                <div className="support-msg__time">{formatTime(m.createdAt, lang)}</div>
              </div>
            ))
          )}
        </div>

        <div className="support-chat__input">
          <input
            className="dialog-input"
            value={draft}
            placeholder={t('support.messagePlaceholder')}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.nativeEvent.isComposing) void handleSend();
            }}
          />
          <button className="icon-btn" onClick={() => void handleSend()} disabled={!draft.trim() || sending} aria-label={t('common.send')} type="button">
            <SendIcon size={18} />
          </button>
        </div>
      </div>
    </div>
  );
}
