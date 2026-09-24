import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  closeSupportTicket,
  getAdminSupportTicket,
  getAdminSupportTickets,
  sendSupportMessage,
} from '../api/conexyApi';
import { signalrService } from '../services/signalrService';
import type { AdminSupportTicket, SupportMessage, SupportTicket } from '../types/api';
import { SendIcon } from './Icons';

interface AdminSupportProps {
  onToast: (message: string) => void;
}

// I18N_FORMAT: добавлено 2026-09-24 — время и дата форматируются по текущему языку интерфейса,
// а не всегда по ru-RU.
function formatTime(iso: string, lang: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleTimeString(lang, { hour: '2-digit', minute: '2-digit' });
}

function formatDateTime(iso: string, lang: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleString(lang, { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
}

/** Appends a message unless one with the same id is already there (HTTP reply + hub echo). */
function withMessage(ticket: SupportTicket, msg: SupportMessage): SupportTicket {
  if (ticket.messages.some((m) => m.id === msg.id)) return ticket;
  return { ...ticket, messages: [...ticket.messages, msg] };
}

// SUPPORT: добавлено 2026-09-19
/** Admin-side support section: ticket list on the left, selected conversation on the right. */
export function AdminSupport({ onToast }: AdminSupportProps) {
  const { t, i18n } = useTranslation();
  const lang = i18n.resolvedLanguage ?? i18n.language;
  const [tickets, setTickets] = useState<AdminSupportTicket[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [selected, setSelected] = useState<SupportTicket | null>(null);
  const [ticketLoading, setTicketLoading] = useState(false);
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<'Open' | ''>('Open');
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);

  // SUPPORT_RACES: добавлено 2026-09-24 (M15) — у каждого запроса свой порядковый номер: ответ,
  // пришедший после того, как админ уже открыл другое обращение (или сменил фильтр), отбрасывается,
  // вместо того чтобы подменить собой то, что сейчас на экране.
  const listSeqRef = useRef(0);
  const openSeqRef = useRef(0);
  // Черновик ответа принадлежит конкретному обращению: при переключении он не должен «переехать»
  // в чужой тикет, но и не теряется — вернётся, когда админ снова откроет это обращение.
  const draftsRef = useRef(new Map<string, string>());

  async function loadTickets() {
    const seq = ++listSeqRef.current;
    try {
      const list = await getAdminSupportTickets(status || undefined, search || undefined);
      if (seq !== listSeqRef.current) return;
      setTickets(list);
    } catch {
      if (seq === listSeqRef.current) onToast(t('support.loadError'));
    }
  }

  useEffect(() => {
    const t = setTimeout(() => void loadTickets(), search ? 300 : 0);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status, search]);

  useEffect(() => {
    const unsubscribe = signalrService.onSupportMessage((msg) => {
      setSelected((s) => (s && s.id === msg.ticketId ? withMessage(s, msg) : s));
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
    if (id === selectedId && selected?.id === id) return;
    const seq = ++openSeqRef.current;

    // Park the draft of the ticket we are leaving and bring back the one of the ticket we open.
    if (selectedId) draftsRef.current.set(selectedId, draft);
    setDraft(draftsRef.current.get(id) ?? '');

    setSelectedId(id);
    // The previous conversation must disappear right away: while it stayed on screen under the
    // new ticket's header, a reply typed during the load went to the previous ticket.
    setSelected(null);
    setTicketLoading(true);
    try {
      const ticket = await getAdminSupportTicket(id);
      if (seq !== openSeqRef.current) return;
      setSelected(ticket);
      await signalrService.joinSupportTicket(id);
    } catch {
      if (seq === openSeqRef.current) onToast(t('support.openTicketError'));
    } finally {
      if (seq === openSeqRef.current) setTicketLoading(false);
    }
  }

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight });
  }, [selected?.id, selected?.messages.length]);

  async function handleSend() {
    const content = draft.trim();
    if (!content || !selected || sending) return;
    // The reply goes to the ticket that is on screen at the moment of sending — captured here, so
    // switching tickets while the request is in flight cannot redirect it.
    const ticketId = selected.id;
    const viewAtSend = openSeqRef.current;
    setSending(true);
    setDraft('');
    draftsRef.current.delete(ticketId);
    try {
      const msg = await sendSupportMessage(ticketId, content);
      // Show our own reply at once (the hub echo is de-duplicated by id).
      if (msg && msg.id) {
        setSelected((s) => (s && s.id === ticketId ? withMessage(s, msg) : s));
        setTickets((list) =>
          list.map((tk) =>
            tk.id === ticketId ? { ...tk, lastMessageAt: msg.createdAt, lastMessagePreview: msg.content } : tk,
          ),
        );
      }
    } catch {
      onToast(t('support.sendError'));
      // Give the text back: into the input if the admin still looks at that ticket, otherwise into
      // that ticket's parked draft.
      if (openSeqRef.current === viewAtSend) setDraft((d) => d || content);
      else draftsRef.current.set(ticketId, content);
    } finally {
      setSending(false);
    }
  }

  async function handleClose() {
    if (!selected) return;
    const ticketId = selected.id;
    try {
      await closeSupportTicket(ticketId);
      onToast(t('support.closedToast'));
      // Only reset the view when it still shows the ticket that was closed.
      setSelected((s) => (s && s.id === ticketId ? null : s));
      setSelectedId((id) => (id === ticketId ? null : id));
      void loadTickets();
    } catch {
      onToast(t('support.closeError'));
    }
  }

  // Header data comes from the ticket that is actually displayed, never from the one being loaded.
  const shownMeta = selected ? tickets.find((tk) => tk.id === selected.id) : undefined;
  const selectedUserName = shownMeta?.userGitHubUsername ?? shownMeta?.userEmail ?? t('support.user');

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
                <div className="admin-support__ticket-time">{formatDateTime(ticket.lastMessageAt, lang)}</div>
              </button>
            ))
          )}
        </div>
      </div>

      <div className="admin-support__chat">
        {!selected ? (
          <p className="muted admin-support__empty">
            {ticketLoading ? t('common.loading') : t('support.selectTicket')}
          </p>
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
                  <div className="support-msg__time">{formatTime(m.createdAt, lang)}</div>
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
                  if (e.key === 'Enter' && !e.nativeEvent.isComposing) void handleSend();
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
