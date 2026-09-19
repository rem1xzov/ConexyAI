import { useEffect, useRef, useState } from 'react';
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
      onToast('Не удалось загрузить обращения');
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
      onToast('Не удалось открыть обращение');
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
      onToast('Не удалось отправить ответ');
    } finally {
      setSending(false);
    }
  }

  async function handleClose() {
    if (!selected) return;
    try {
      await closeSupportTicket(selected.id);
      onToast('Обращение закрыто');
      setSelected(null);
      setSelectedId(null);
      void loadTickets();
    } catch {
      onToast('Не удалось закрыть обращение');
    }
  }

  const selectedMeta = tickets.find((t) => t.id === selectedId);
  const selectedUserName = selectedMeta?.userGitHubUsername ?? selectedMeta?.userEmail ?? 'Пользователь';

  return (
    <div className="admin-support">
      <div className="admin-support__list">
        <div className="admin-support__filters">
          <input
            className="dialog-input"
            value={search}
            placeholder="Поиск по email/юзернейму"
            onChange={(e) => setSearch(e.target.value)}
          />
          <select
            className="dialog-input"
            value={status}
            onChange={(e) => setStatus(e.target.value as 'Open' | '')}
          >
            <option value="Open">Открытые</option>
            <option value="">Все</option>
          </select>
        </div>

        <div className="admin-support__tickets">
          {tickets.length === 0 ? (
            <p className="muted">Нет обращений</p>
          ) : (
            tickets.map((t) => (
              <button
                key={t.id}
                className={`admin-support__ticket ${t.id === selectedId ? 'admin-support__ticket--active' : ''}`}
                onClick={() => void openTicket(t.id)}
                type="button"
              >
                <div className="admin-support__ticket-name">{t.userGitHubUsername ?? t.userEmail ?? 'Пользователь'}</div>
                <div className="admin-support__ticket-preview">{t.lastMessagePreview ?? '—'}</div>
                <div className="admin-support__ticket-time">{formatDateTime(t.lastMessageAt)}</div>
              </button>
            ))
          )}
        </div>
      </div>

      <div className="admin-support__chat">
        {!selected ? (
          <p className="muted admin-support__empty">Выберите обращение слева</p>
        ) : (
          <>
            <div className="admin-support__chat-head">
              <span className="admin-support__chat-user">{selectedUserName}</span>
              <span
                className={`admin-support__chat-status ${selected.status === 'Open' ? 'admin-support__chat-status--open' : ''}`}
              >
                {selected.status === 'Open' ? 'Открыт' : 'Закрыт'}
              </span>
              {selected.status === 'Open' && (
                <button className="admin-btn" onClick={() => void handleClose()} type="button">
                  Закрыть
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
                placeholder="Ответ…"
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
                aria-label="Отправить"
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
