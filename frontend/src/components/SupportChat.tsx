import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  cancelSupportTicket,
  createNewSupportTicket,
  escalateSupportTicket,
  getSupportTicket,
  listSupportTickets,
  returnSupportTicketToBot,
  sendSupportMessage,
  sendSupportMessageWithFiles,
} from '../api/conexyApi';
import { signalrService } from '../services/signalrService';
import type { SupportMessage, SupportTicket, SupportTicketSummary } from '../types/api';
import { ConfirmDialog } from './Dialog';
import { CloseIcon, PlusIcon } from './Icons';
import { Composer, formatListTime, MessageList, withMessage } from './support/SupportParts';

interface SupportChatProps {
  onClose: () => void;
  onToast: (message: string) => void;
}

const NEW = 'new';

/** Текст ошибки с сервера ({ code, message }) или запасной перевод. */
function errorText(e: unknown, fallback: string): string {
  const message = (e as { response?: { data?: { message?: string } } }).response?.data?.message;
  return typeof message === 'string' && message.length > 0 ? message : fallback;
}

function toSummary(t: SupportTicket): SupportTicketSummary {
  const last = t.messages[t.messages.length - 1];
  return {
    id: t.id,
    subject: t.subject ?? null,
    status: t.status,
    botActive: t.botActive,
    createdAt: t.createdAt,
    lastMessageAt: last?.createdAt ?? t.lastMessageAt,
    lastMessagePreview: last ? last.content || '📎' : null,
    lastMessageAuthor: last?.authorType ?? null,
    unreadCount: 0,
  };
}

// SUPPORT: добавлено 2026-09-19
// SUPPORT_MULTI: переделано 2026-10-18 — мессенджер: список обращений слева (на телефоне — отдельный
// экран), переписка справа, сколько угодно обращений сразу, картинки и файлы во вложениях.
export function SupportChat({ onClose, onToast }: SupportChatProps) {
  const { t, i18n } = useTranslation();
  const lang = i18n.resolvedLanguage ?? i18n.language;
  const [tickets, setTickets] = useState<SupportTicketSummary[] | null>(null);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [active, setActive] = useState<SupportTicket | null>(null);
  const [loadingTicket, setLoadingTicket] = useState(false);
  const [botTyping, setBotTyping] = useState(false);
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState<'escalate' | 'close' | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const activeIdRef = useRef<string | null>(null);
  const joined = useRef(new Set<string>());
  activeIdRef.current = activeId;

  function join(id: string) {
    if (joined.current.has(id)) return;
    joined.current.add(id);
    void signalrService.joinSupportTicket(id).catch(() => joined.current.delete(id));
  }

  async function refreshList(): Promise<SupportTicketSummary[]> {
    const list = await listSupportTickets();
    setTickets(list);
    list.filter((x) => x.status !== 'Closed').forEach((x) => join(x.id));
    return list;
  }

  // Открытие: список обращений; на широком экране сразу открываем самое свежее открытое.
  useEffect(() => {
    let cancelled = false;
    const unsubscribe = signalrService.onSupportMessage((msg: SupportMessage) => {
      const isActive = activeIdRef.current === msg.ticketId;
      if (isActive) {
        setActive((a) => (a && a.id === msg.ticketId ? withMessage(a, msg) : a));
        if (msg.authorType !== 'User') setBotTyping(false);
      }
      setTickets((list) => {
        if (!list) return list;
        const updated = list.map((x) =>
          x.id === msg.ticketId
            ? {
                ...x,
                lastMessageAt: msg.createdAt,
                lastMessagePreview: msg.content || '📎',
                lastMessageAuthor: msg.authorType,
                unreadCount: !isActive && (msg.authorType === 'Bot' || msg.authorType === 'Admin') ? x.unreadCount + 1 : x.unreadCount,
              }
            : x,
        );
        return [...updated].sort((a, b) => b.lastMessageAt.localeCompare(a.lastMessageAt));
      });
    });

    void (async () => {
      try {
        const list = await refreshList();
        if (cancelled) return;
        const wide = window.matchMedia('(min-width: 721px)').matches;
        const firstOpen = list.find((x) => x.status !== 'Closed');
        if (list.length === 0) setActiveId(NEW);
        else if (wide && firstOpen) void openTicket(firstOpen.id);
      } catch {
        if (!cancelled) onToast(t('support.openError'));
      }
    })();

    const joinedSet = joined.current;
    return () => {
      cancelled = true;
      unsubscribe();
      joinedSet.forEach((id) => void signalrService.leaveSupportTicket(id));
      joinedSet.clear();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight });
  }, [active?.id, active?.messages.length, botTyping]);

  async function openTicket(id: string) {
    setActiveId(id);
    setBotTyping(false);
    if (id === NEW) {
      setActive(null);
      return;
    }
    setLoadingTicket(true);
    setActive(null);
    try {
      const ticket = await getSupportTicket(id);
      if (activeIdRef.current !== id) return;
      setActive(ticket);
      join(id);
      setTickets((list) => list?.map((x) => (x.id === id ? { ...x, unreadCount: 0 } : x)) ?? list);
    } catch (e) {
      onToast(errorText(e, t('support.openTicketError')));
    } finally {
      setLoadingTicket(false);
    }
  }

  async function send(text: string, files: File[], onProgress: (p: number) => void): Promise<boolean> {
    try {
      let ticket = active;
      // Новое обращение создаётся только с первым сообщением — пустых обращений не бывает.
      if (!ticket) {
        ticket = await createNewSupportTicket();
        setActiveId(ticket.id);
        activeIdRef.current = ticket.id;
        setActive(ticket);
        join(ticket.id);
        setTickets((list) => [toSummary(ticket!), ...(list ?? [])]);
      }
      const id = ticket.id;
      if (ticket.botActive) setBotTyping(true);
      const msg = files.length > 0
        ? await sendSupportMessageWithFiles(id, text, files, false, onProgress)
        : await sendSupportMessage(id, text);
      setActive((a) => (a && a.id === id ? withMessage(a, msg) : a));
      // Ответ бота пишется в том же запросе, но эхо хаба может опередить подписку — перечитываем.
      const fresh = await getSupportTicket(id);
      if (activeIdRef.current === id) setActive(fresh);
      setTickets((list) => {
        const rest = (list ?? []).filter((x) => x.id !== id);
        return [toSummary(fresh), ...rest];
      });
      return true;
    } catch (e) {
      onToast(errorText(e, t('support.sendError')));
      return false;
    } finally {
      setBotTyping(false);
    }
  }

  async function transition(fn: (id: string) => Promise<SupportTicket>) {
    if (!active || busy) return;
    setBusy(true);
    try {
      const updated = await fn(active.id);
      setActive(updated);
      setTickets((list) => list?.map((x) => (x.id === updated.id ? { ...toSummary(updated) } : x)) ?? list);
    } catch (e) {
      onToast(errorText(e, t('support.actionError')));
    } finally {
      setBusy(false);
    }
  }

  const openTickets = useMemo(() => (tickets ?? []).filter((x) => x.status !== 'Closed'), [tickets]);
  const closedTickets = useMemo(() => (tickets ?? []).filter((x) => x.status === 'Closed'), [tickets]);
  const status = active?.status ?? '';
  const readOnly = status === 'Closed';
  const showThread = activeId !== null;

  function statusLabel(s: string, botActive: boolean) {
    if (s === 'Closed') return t('support.statusClosed');
    if (s === 'Escalated' && !botActive) return t('support.statusEscalated');
    return t('support.statusBot');
  }

  function authorPrefix(a: string | null) {
    if (a === 'User') return t('support.you') + ': ';
    if (a === 'Admin') return t('support.operatorName') + ': ';
    if (a === 'Bot') return t('support.botName') + ': ';
    return '';
  }

  function TicketRow({ x }: { x: SupportTicketSummary }) {
    return (
      <button
        type="button"
        className={`support-list__item ${x.id === activeId ? 'support-list__item--active' : ''}`}
        onClick={() => void openTicket(x.id)}
      >
        <span className={`support-list__dot support-list__dot--${x.status === 'Closed' ? 'closed' : x.status === 'Escalated' && !x.botActive ? 'operator' : 'bot'}`} />
        <span className="support-list__body">
          <span className="support-list__top">
            <span className="support-list__subject">{x.subject || t('support.newTicket')}</span>
            <span className="support-list__time">{formatListTime(x.lastMessageAt, lang, t('support.yesterday'))}</span>
          </span>
          <span className="support-list__bottom">
            <span className="support-list__preview">
              {x.lastMessagePreview ? authorPrefix(x.lastMessageAuthor) + x.lastMessagePreview : t('support.noMessages')}
            </span>
            {x.unreadCount > 0 && <span className="support-list__badge">{x.unreadCount}</span>}
          </span>
        </span>
      </button>
    );
  }

  return (
    <div className="dialog-overlay support-overlay" onMouseDown={onClose}>
      <div
        className={`dialog-card support-messenger ${showThread ? 'support-messenger--thread' : ''}`}
        role="dialog"
        aria-modal="true"
        aria-label={t('support.title')}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <aside className="support-list">
          <div className="support-list__head">
            <div className="dialog-title">{t('support.title')}</div>
            <div className="support-list__head-actions">
              <button className="support-new" type="button" onClick={() => void openTicket(NEW)}>
                <PlusIcon size={16} />
                <span>{t('support.newTicketShort')}</span>
              </button>
              <button className="icon-btn support-list__close" onClick={onClose} aria-label={t('common.close')} type="button">
                <CloseIcon size={18} />
              </button>
            </div>
          </div>
          <div className="support-list__items">
            {tickets === null ? (
              <p className="muted support-list__empty">{t('common.loading')}</p>
            ) : tickets.length === 0 ? (
              <p className="muted support-list__empty">{t('support.noTicketsYet')}</p>
            ) : (
              <>
                {openTickets.map((x) => (
                  <TicketRow key={x.id} x={x} />
                ))}
                {closedTickets.length > 0 && <div className="support-list__section">{t('support.closedSection')}</div>}
                {closedTickets.map((x) => (
                  <TicketRow key={x.id} x={x} />
                ))}
              </>
            )}
          </div>
        </aside>

        <section className="support-thread">
          <div className="support-thread__head">
            <button className="icon-btn support-thread__back" type="button" onClick={() => setActiveId(null)} aria-label={t('support.back')}>
              ‹
            </button>
            <div className="support-thread__title">
              <div className="support-thread__subject">
                {activeId === NEW || !active ? t('support.newTicket') : active.subject || t('support.newTicket')}
              </div>
              {active && <div className="support-thread__status">{statusLabel(active.status, active.botActive)}</div>}
            </div>
            <button className="icon-btn support-thread__close" onClick={onClose} aria-label={t('common.close')} type="button">
              <CloseIcon size={18} />
            </button>
          </div>

          {activeId === null ? (
            <div className="support-thread__placeholder muted">{t('support.selectOrCreate')}</div>
          ) : loadingTicket ? (
            <div className="support-thread__placeholder muted">{t('common.loading')}</div>
          ) : (
            <MessageList
              messages={active?.messages ?? []}
              operatorView={false}
              lang={lang}
              scrollRef={scrollRef}
              typing={botTyping}
              empty={<p className="muted support-thread__hint">{t('support.hint')}</p>}
            />
          )}

          {active && !readOnly && (
            <div className="support-chat__actions">
              {active.botActive && (
                <button className="support-action" onClick={() => setConfirm('escalate')} disabled={busy} type="button">
                  {t('support.notHelpful')}
                </button>
              )}
              {status === 'Escalated' && !active.botActive && (
                <button className="support-action" onClick={() => void transition(returnSupportTicketToBot)} disabled={busy} type="button">
                  {t('support.returnToBot')}
                </button>
              )}
              <button className="support-action support-action--danger" onClick={() => setConfirm('close')} disabled={busy} type="button">
                {t('support.cancelRequest')}
              </button>
            </div>
          )}

          {readOnly ? (
            <div className="support-thread__closed">
              <span className="muted">{t('support.closedPlaceholder')}</span>
              <button className="support-action" type="button" onClick={() => void openTicket(NEW)}>
                {t('support.newTicketShort')}
              </button>
            </div>
          ) : (
            activeId !== null && !loadingTicket && (
              <Composer placeholder={t('support.messagePlaceholder')} onSend={send} onToast={onToast} />
            )
          )}
        </section>

        {confirm && (
          <ConfirmDialog
            title={confirm === 'escalate' ? t('support.escalateConfirmTitle') : t('support.closeConfirmTitle')}
            message={confirm === 'escalate' ? t('support.escalateConfirm') : t('support.closeConfirm')}
            confirmLabel={confirm === 'escalate' ? t('support.escalateConfirmYes') : t('support.cancelRequest')}
            onConfirm={() => {
              const what = confirm;
              setConfirm(null);
              void transition(what === 'escalate' ? escalateSupportTicket : cancelSupportTicket);
            }}
            onCancel={() => setConfirm(null)}
          />
        )}
      </div>
    </div>
  );
}
