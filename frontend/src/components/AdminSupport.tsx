import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  closeSupportTicket,
  getAdminSupportTicket,
  getAdminSupportTickets,
  sendAdminSupportMessage,
  sendSupportMessageWithFiles,
} from '../api/conexyApi';
import { signalrService } from '../services/signalrService';
import type { AdminSupportTicket, SupportTicket } from '../types/api';
import { Composer, MessageList, withMessage } from './support/SupportParts';

interface AdminSupportProps {
  onToast: (message: string) => void;
}

// I18N_FORMAT: добавлено 2026-09-24 — дата форматируется по текущему языку интерфейса.
function formatDateTime(iso: string, lang: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleString(lang, { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
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
  // SUPPORT_BOT: по умолчанию показываем эскалированные обращения — по ним нужен оператор.
  const [status, setStatus] = useState<'Escalated' | 'BotHandling' | 'Closed' | ''>('Escalated');
  const scrollRef = useRef<HTMLDivElement>(null);

  // SUPPORT_RACES: добавлено 2026-09-24 (M15) — у каждого запроса свой порядковый номер: ответ,
  // пришедший после того, как админ уже открыл другое обращение (или сменил фильтр), отбрасывается,
  // вместо того чтобы подменить собой то, что сейчас на экране.
  const listSeqRef = useRef(0);
  const openSeqRef = useRef(0);

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

  // Ответ уходит в то обращение, что было на экране в момент отправки (id захвачен до await).
  // При ошибке Composer не чистит текст и файлы — ничего не теряется.
  async function handleSend(content: string, files: File[], onProgress: (p: number) => void): Promise<boolean> {
    if (!selected) return false;
    const ticketId = selected.id;
    try {
      const msg = files.length > 0
        ? await sendSupportMessageWithFiles(ticketId, content, files, true, onProgress)
        : await sendAdminSupportMessage(ticketId, content);
      if (msg && msg.id) {
        setSelected((s) => (s && s.id === ticketId ? withMessage(s, msg) : s));
        setTickets((list) =>
          list.map((tk) =>
            tk.id === ticketId ? { ...tk, lastMessageAt: msg.createdAt, lastMessagePreview: msg.content || '📎' } : tk,
          ),
        );
      }
      return true;
    } catch (e) {
      const message = (e as { response?: { data?: { message?: string } } }).response?.data?.message;
      onToast(message || t('support.sendError'));
      return false;
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
            onChange={(e) => setStatus(e.target.value as 'Escalated' | 'BotHandling' | 'Closed' | '')}
          >
            <option value="Escalated">{t('support.statusEscalated')}</option>
            <option value="BotHandling">{t('support.statusBot')}</option>
            <option value="Closed">{t('support.statusClosed')}</option>
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
                {ticket.subject && <div className="admin-support__ticket-subject">{ticket.subject}</div>}
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
                className={`admin-support__chat-status ${selected.status !== 'Closed' ? 'admin-support__chat-status--open' : ''}`}
              >
                {selected.status === 'Closed'
                  ? t('support.statusClosed')
                  : selected.status === 'Escalated'
                    ? t('support.statusEscalated')
                    : t('support.statusBot')}
              </span>
              {selected.status !== 'Closed' && (
                <button className="admin-btn" onClick={() => void handleClose()} type="button">
                  {t('support.close')}
                </button>
              )}
            </div>

            {selected.subject && <div className="admin-support__chat-subject">{selected.subject}</div>}
            <MessageList messages={selected.messages} operatorView lang={lang} scrollRef={scrollRef} />

            {/* key: у каждого обращения свой черновик — при переключении текст не «переезжает» в чужой тикет. */}
            <Composer
              key={selected.id}
              placeholder={t('support.replyPlaceholder')}
              disabled={selected.status === 'Closed'}
              onSend={handleSend}
              onToast={onToast}
            />
          </>
        )}
      </div>
    </div>
  );
}
