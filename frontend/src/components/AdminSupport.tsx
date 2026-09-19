import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  closeSupportTicket,
  getAdminSupportTicket,
  getAdminSupportTickets,
  sendSupportMessage,
} from '../api/conexyApi';
import { signalrService } from '../services/signalrService';
import type { AdminSupportTicket, SupportTicket } from '../types/api';
import { SendIcon } from './Icons';

interface AdminSupportProps {
  onToast: (message: string) => void;
}

function formatTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
}

function formatDateTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
}

// SUPPORT: добавлено 2026-09-19
/** Admin-side support section: ticket list on the left, selected conversation on the right. */
export function AdminSupport({ onToast }: AdminSupportProps) {
  const { t } = useTranslation();
  const [tickets, setTickets] = useState<AdminSupportTicket[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [selected, setSelected] = useState<SupportTicket | null>(null);
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<'Open' | ''>('Open');
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);

  async function loadTickets() {
    try {
      const list = await getAdminSupportTickets(status || undefined, search || undefined);
      setTickets(list);
    } catch {
      onToast(t('support.loadError'));
    }
  }

  useEffect(() => {
    const t = setTimeout(() => void loadTickets(), search ? 300 : 0);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status, search]);

  useEffect(() => {
    const unsubscribe = signalrService.onSupportMessage((msg) => {
      setSelected((s) => (s && s.id === msg.ticketId ? { ...s, messages: [...s.messages, msg] } : s));
      setTickets((list) =>
        list.map((t) =>
          t.id === msg.ticketId
            ? { ...t, lastMessageAt: msg.createdAt, lastMessagePreview: msg.content }
            : t,
        ),
      );
    });
    return unsubscribe;
  }, []);

  async function openTicket(id: string) {
    setSelectedId(id);
    try {
      const t = await getAdminSupportTicket(id);
      setSelected(t);
      await signalrService.joinSupportTicket(id);
    } catch {
      onToast(t('support.openTicketError'));
    }
  }

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight });
  }, [selected?.messages.length]);

  async function handleSend() {
    const content = draft.trim();
    if (!content || !selected || sending) return;
    setSending(true);
    setDraft('');
    try {
      await sendSupportMessage(selected.id, content);
    } catch {
      onToast(t('support.sendError'));
    } finally {
      setSending(false);
    }
  }

  async function handleClose() {
    if (!selected) return;
    try {
      await closeSupportTicket(selected.id);
      onToast(t('support.closedToast'));
      setSelected(null);
      setSelectedId(null);
      void loadTickets();
    } catch {
      onToast(t('support.closeError'));
    }
  }

  const selectedMeta = tickets.find((t) => t.id === selectedId);
  const selectedUserName = selectedMeta?.userGitHubUsername ?? selectedMeta?.userEmail ?? t('support.user');

  return (
    <div className="admin-support">
      <div className="admin-support__list">
        <div className="admin-support__filters">
          <input
            className="dialog-input"
            value={search}
            placeholder={t('support.searchPlaceholder')}
            onChange={(e) => setSearch(e.target.value)}
          />
          <select
            className="dialog-input"
            value={status}
            onChange={(e) => setStatus(e.target.value as 'Open' | '')}
          >
            <option value="Open">{t('support.openFilter')}</option>
            <option value="">{t('support.allFilter')}</option>
          </select>
        </div>

        <div className="admin-support__tickets">
          {tickets.length === 0 ? (
            <p className="muted">{t('support.noTickets')}</p>
          ) : (
            tickets.map((ticket) => (
              <button
                key={ticket.id}
                className={`admin-support__ticket ${ticket.id === selectedId ? 'admin-support__ticket--active' : ''}`}
                onClick={() => void openTicket(ticket.id)}
                type="button"
              >
                <div className="admin-support__ticket-name">{ticket.userGitHubUsername ?? ticket.userEmail ?? t('support.user')}</div>
                <div className="admin-support__ticket-preview">{ticket.lastMessagePreview ?? '—'}</div>
                <div className="admin-support__ticket-time">{formatDateTime(ticket.lastMessageAt)}</div>
              </button>
            ))
          )}
        </div>
      </div>

      <div className="admin-support__chat">
        {!selected ? (
          <p className="muted admin-support__empty">{t('support.selectTicket')}</p>
        ) : (
          <>
            <div className="admin-support__chat-head">
              <span className="admin-support__chat-user">{selectedUserName}</span>
              <span
                className={`admin-support__chat-status ${selected.status === 'Open' ? 'admin-support__chat-status--open' : ''}`}
              >
                {selected.status === 'Open' ? t('support.open') : t('support.closed')}
              </span>
              {selected.status === 'Open' && (
                <button className="admin-btn" onClick={() => void handleClose()} type="button">
                  {t('support.close')}
                </button>
              )}
            </div>

            <div className="admin-support__messages" ref={scrollRef}>
              {selected.messages.map((m) => (
                <div key={m.id} className={`support-msg ${m.isFromAdmin ? 'support-msg--own' : 'support-msg--other'}`}>
                  <div className="support-msg__bubble">{m.content}</div>
                  <div className="support-msg__time">{formatTime(m.createdAt)}</div>
                </div>
              ))}
            </div>

            <div className="support-chat__input">
              <input
                className="dialog-input"
                value={draft}
                placeholder={t('support.replyPlaceholder')}
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') void handleSend();
                }}
                disabled={selected.status !== 'Open'}
              />
              <button
                className="icon-btn"
                onClick={() => void handleSend()}
                disabled={!draft.trim() || sending || selected.status !== 'Open'}
                aria-label={t('common.send')}
                type="button"
              >
                <SendIcon size={18} />
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
