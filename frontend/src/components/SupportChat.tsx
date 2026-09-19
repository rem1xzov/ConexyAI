import { useEffect, useRef, useState } from 'react';
import { createSupportTicket, sendSupportMessage } from '../api/conexyApi';
import { signalrService } from '../services/signalrService';
import type { SupportMessage, SupportTicket } from '../types/api';
import { CloseIcon, SendIcon } from './Icons';

interface SupportChatProps {
  onClose: () => void;
  onToast: (message: string) => void;
}

function formatTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
}

// SUPPORT: добавлено 2026-09-19
/** User-facing messenger-style support chat. */
export function SupportChat({ onClose, onToast }: SupportChatProps) {
  const [ticket, setTicket] = useState<SupportTicket | null>(null);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let cancelled = false;
    let ticketId: string | null = null;

    const unsubscribe = signalrService.onSupportMessage((msg) => {
      setTicket((t) => (t && t.id === msg.ticketId ? { ...t, messages: [...t.messages, msg] } : t));
    });

    async function init() {
      try {
        const t = await createSupportTicket();
        if (cancelled) return;
        ticketId = t.id;
        setTicket(t);
        await signalrService.joinSupportTicket(t.id);
      } catch {
        if (!cancelled) onToast('Не удалось открыть поддержку');
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
    setSending(true);
    setDraft('');
    try {
      await sendSupportMessage(ticket.id, content);
    } catch {
      onToast('Не удалось отправить сообщение');
    } finally {
      setSending(false);
    }
  }

  return (
    <div className="dialog-overlay" onMouseDown={onClose}>
      <div className="dialog-card support-chat" role="dialog" aria-modal="true" onMouseDown={(e) => e.stopPropagation()}>
        <div className="support-chat__head">
          <div className="dialog-title">Поддержка</div>
          <button className="icon-btn" onClick={onClose} aria-label="Закрыть" type="button">
            <CloseIcon size={18} />
          </button>
        </div>

        <div className="support-chat__messages" ref={scrollRef}>
          {!ticket ? (
            <p className="muted">Загрузка…</p>
          ) : ticket.messages.length === 0 ? (
            <p className="muted support-chat__hint">Опишите вашу проблему — администратор ответит здесь.</p>
          ) : (
            ticket.messages.map((m: SupportMessage) => (
              <div key={m.id} className={`support-msg ${m.isFromAdmin ? 'support-msg--other' : 'support-msg--own'}`}>
                <div className="support-msg__bubble">{m.content}</div>
                <div className="support-msg__time">{formatTime(m.createdAt)}</div>
              </div>
            ))
          )}
        </div>

        <div className="support-chat__input">
          <input
            className="dialog-input"
            value={draft}
            placeholder="Сообщение…"
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') void handleSend();
            }}
          />
          <button className="icon-btn" onClick={() => void handleSend()} disabled={!draft.trim() || sending} aria-label="Отправить" type="button">
            <SendIcon size={18} />
          </button>
        </div>
      </div>
    </div>
  );
}
