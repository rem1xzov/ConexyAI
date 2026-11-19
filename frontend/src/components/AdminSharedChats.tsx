import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  closeAdminSharedChat,
  getAdminSharedChat,
  getAdminSharedChats,
} from '../api/conexyApi';
import type {
  AdminSharedChatAgentEvent,
  AdminSharedChatDetail,
  AdminSharedChatSummary,
  SharedChatReportStatus,
} from '../types/api';
import { humanError } from '../utils/humanError';
import { Markdown } from './Markdown';

const PAGE_SIZE = 20;
// Длинный вывод инструмента (например, diff или лог сборки) не должен раздувать карточку: показываем
// начало, остальное — по кнопке «Показать полностью».
const OUTPUT_PREVIEW_CHARS = 600;

type SharedStatusFilter = 'open' | 'closed' | 'all';

interface AdminSharedChatsProps {
  onToast: (message: string) => void;
}

function formatDateTime(iso: string, lang: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleString(lang === 'en' ? 'en-US' : 'ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

/** Раскрывающийся блок для длинных arguments/output событий агента. */
function OutputBox({ text }: { text: string }) {
  const { t } = useTranslation();
  const [expanded, setExpanded] = useState(false);
  const long = text.length > OUTPUT_PREVIEW_CHARS || text.split('\n').length > 8;
  const shown = long && !expanded ? `${text.slice(0, OUTPUT_PREVIEW_CHARS)}…` : text;

  return (
    <>
      <pre className="admin-shared__pre">{shown}</pre>
      {long && (
        <button
          className="admin-shared__more"
          type="button"
          onClick={() => setExpanded((value) => !value)}
        >
          {expanded ? t('admin.shared.collapse') : t('admin.shared.expand')}
        </button>
      )}
    </>
  );
}

function AgentEventRow({ event, lang }: { event: AdminSharedChatAgentEvent; lang: string }) {
  const { t } = useTranslation();
  return (
    <div className={`admin-shared__event ${event.isError ? 'admin-shared__event--error' : ''}`}>
      <div className="admin-shared__event-head">
        <span className="admin-shared__event-seq">#{event.seq}</span>
        <span className="admin-shared__event-kind">{event.kind}</span>
        {event.tool && <span className="admin-shared__event-tool">{event.tool}</span>}
        <span className="admin-shared__event-time">{formatDateTime(event.createdAt, lang)}</span>
      </div>
      {event.arguments && (
        <div className="admin-shared__event-block">
          <span className="admin-shared__event-caption">{t('admin.shared.arguments')}</span>
          <OutputBox text={event.arguments} />
        </div>
      )}
      {event.output && (
        <div className="admin-shared__event-block">
          <span className="admin-shared__event-caption">{t('admin.shared.output')}</span>
          <OutputBox text={event.output} />
        </div>
      )}
    </div>
  );
}

// SHARE_ADMIN: админская вкладка «Жалобы» — слева список переданных пользователями чатов, справа
// переписка, хронология действий агента, скриншот и закрытие жалобы.
export function AdminSharedChats({ onToast }: AdminSharedChatsProps) {
  const { t, i18n } = useTranslation();
  const lang = i18n.resolvedLanguage ?? i18n.language;

  const [status, setStatus] = useState<SharedStatusFilter>('open');
  const [page, setPage] = useState(1);
  const [reports, setReports] = useState<AdminSharedChatSummary[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [listLoading, setListLoading] = useState(true);
  const [listError, setListError] = useState<string | null>(null);

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [selected, setSelected] = useState<AdminSharedChatDetail | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);

  const [closeOpen, setCloseOpen] = useState(false);
  const [closeNote, setCloseNote] = useState('');
  const [closing, setClosing] = useState(false);

  // SHARE_ADMIN_RACES: ответы list/detail нумеруются — поздний ответ для другой страницы, другого
  // фильтра или другой жалобы не должен подменить то, что админ видит сейчас (как в AdminSupport).
  const listSeqRef = useRef(0);
  const openSeqRef = useRef(0);

  async function loadReports(targetPage: number) {
    const seq = ++listSeqRef.current;
    setListLoading(true);
    setListError(null);
    try {
      const response = await getAdminSharedChats(targetPage, PAGE_SIZE, status);
      if (seq !== listSeqRef.current) return;
      setReports(Array.isArray(response?.items) ? response.items : []);
      setTotalCount(Number.isFinite(response?.totalCount) ? response.totalCount : 0);
      setPage(Number.isFinite(response?.page) ? response.page : targetPage);
    } catch (e) {
      if (seq !== listSeqRef.current) return;
      const errorStatus = (e as { response?: { status?: number } })?.response?.status;
      setListError(errorStatus === 403 ? t('admin.noAccess') : humanError(e, t));
    } finally {
      if (seq === listSeqRef.current) setListLoading(false);
    }
  }

  useEffect(() => {
    void loadReports(page);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status, page]);

  function changeStatus(next: SharedStatusFilter) {
    setStatus(next);
    setPage(1);
  }

  async function openReport(id: string, force = false) {
    if (!force && id === selectedId && selected?.id === id) return;
    const seq = ++openSeqRef.current;

    setSelectedId(id);
    // Прошлая жалоба исчезает сразу: пока она висела под новым заголовком, скриншот и заметка
    // выглядели как относящиеся к другой жалобе.
    setSelected(null);
    setDetailError(null);
    setDetailLoading(true);
    setCloseOpen(false);
    setCloseNote('');
    try {
      const detail = await getAdminSharedChat(id);
      if (seq !== openSeqRef.current) return;
      setSelected(detail);
    } catch (e) {
      if (seq !== openSeqRef.current) return;
      const errorStatus = (e as { response?: { status?: number } })?.response?.status;
      setDetailError(errorStatus === 403 ? t('admin.noAccess') : humanError(e, t));
    } finally {
      if (seq === openSeqRef.current) setDetailLoading(false);
    }
  }

  async function handleClose() {
    if (!selected || closing) return;
    const id = selected.id;
    setClosing(true);
    try {
      await closeAdminSharedChat(id, closeNote);
      onToast(t('admin.shared.closedToast'));
      // Обновляем только то, что закрыто: перезапрашивать переписку после закрытия не нужно.
      setSelected((current) =>
        current && current.id === id
          ? { ...current, status: 'closed', adminNote: closeNote.trim() || current.adminNote }
          : current,
      );
      setReports((list) =>
        list.map((report) => (report.id === id ? { ...report, status: 'closed' as SharedChatReportStatus } : report)),
      );
      setCloseOpen(false);
      setCloseNote('');
      // На фильтре «Открытые» закрытая жалоба должна уйти из списка.
      void loadReports(page);
    } catch (e) {
      const errorStatus = (e as { response?: { status?: number } })?.response?.status;
      onToast(errorStatus === 403 ? t('admin.noAccess') : humanError(e, t));
    } finally {
      setClosing(false);
    }
  }

  function userName(report: Pick<AdminSharedChatSummary, 'userGitHubUsername' | 'userEmail'> | null): string {
    return report?.userGitHubUsername ?? report?.userEmail ?? t('support.user');
  }

  function statusLabel(value: SharedChatReportStatus): string {
    return value === 'closed' ? t('admin.shared.statusClosed') : t('admin.shared.statusOpen');
  }

  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));
  // Ответ без массивов не должен ронять рендер всей панели — показываем пустые секции.
  const selectedMessages =
    selected && Array.isArray(selected.messages) ? selected.messages : [];
  const sortedEvents =
    selected && Array.isArray(selected.agentEvents)
      ? [...selected.agentEvents].sort((a, b) => a.seq - b.seq)
      : [];
  const screenshotSrc =
    selected?.screenshotBase64 && selected.screenshotContentType
      ? `data:${selected.screenshotContentType};base64,${selected.screenshotBase64}`
      : null;

  return (
    <div className="admin-support admin-shared">
      <div className="admin-support__list">
        <div className="admin-support__filters">
          <select
            className="dialog-input"
            value={status}
            onChange={(e) => changeStatus(e.target.value as SharedStatusFilter)}
          >
            <option value="open">{t('admin.shared.filterOpen')}</option>
            <option value="closed">{t('admin.shared.filterClosed')}</option>
            <option value="all">{t('admin.shared.filterAll')}</option>
          </select>
        </div>

        <div className="admin-support__tickets">
          {listLoading ? (
            <p className="muted">{t('common.loading')}</p>
          ) : listError ? (
            <div className="admin__error-block">
              <p className="admin__error">{listError}</p>
              <div className="admin__error-actions">
                <button className="admin-btn" type="button" onClick={() => void loadReports(page)}>
                  {t('admin.retry')}
                </button>
              </div>
            </div>
          ) : reports.length === 0 ? (
            <p className="muted">{t('admin.shared.empty')}</p>
          ) : (
            reports.map((report) => (
              <button
                key={report.id}
                className={`admin-support__ticket ${report.id === selectedId ? 'admin-support__ticket--active' : ''}`}
                onClick={() => void openReport(report.id)}
                type="button"
              >
                <div className="admin-shared__row-head">
                  <span className="admin-support__ticket-name">{userName(report)}</span>
                  <span className={`admin-shared__status admin-shared__status--${report.status}`}>
                    {statusLabel(report.status)}
                  </span>
                </div>
                <div className="admin-support__ticket-subject">
                  {report.chatTitle || t('admin.shared.untitledChat')}
                </div>
                {report.comment && <div className="admin-support__ticket-preview">{report.comment}</div>}
                <div className="admin-shared__row-meta">
                  {report.hasScreenshot && (
                    <span className="admin-shared__badge">📷 {t('admin.shared.screenshotBadge')}</span>
                  )}
                  {report.agentEventCount > 0 && (
                    <span className="admin-shared__badge">
                      {t('admin.shared.events', { count: report.agentEventCount })}
                    </span>
                  )}
                  <span className="admin-support__ticket-time">
                    {formatDateTime(report.createdAt, lang)}
                  </span>
                </div>
              </button>
            ))
          )}
        </div>

        {totalPages > 1 && (
          <div className="admin-shared__pager">
            <button
              className="admin-btn"
              type="button"
              disabled={page <= 1 || listLoading}
              onClick={() => setPage((value) => Math.max(1, value - 1))}
            >
              {t('admin.shared.prev')}
            </button>
            <span className="admin-user__meta">
              {page} / {totalPages}
            </span>
            <button
              className="admin-btn"
              type="button"
              disabled={page >= totalPages || listLoading}
              onClick={() => setPage((value) => value + 1)}
            >
              {t('admin.shared.next')}
            </button>
          </div>
        )}
      </div>

      <div className="admin-support__chat">
        {!selectedId ? (
          <p className="muted admin-support__empty">{t('admin.shared.selectReport')}</p>
        ) : detailLoading && !selected ? (
          <p className="muted admin-support__empty">{t('common.loading')}</p>
        ) : detailError && !selected ? (
          <div className="admin__error-block">
            <p className="admin__error">{detailError}</p>
            <div className="admin__error-actions">
              <button className="admin-btn" type="button" onClick={() => void openReport(selectedId, true)}>
                {t('admin.retry')}
              </button>
            </div>
          </div>
        ) : selected ? (
          <>
            <div className="admin-support__chat-head">
              <span className="admin-support__chat-user">{userName(selected)}</span>
              <span className={`admin-shared__status admin-shared__status--${selected.status}`}>
                {statusLabel(selected.status)}
              </span>
              {selected.status === 'open' && (
                <button
                  className="admin-btn"
                  type="button"
                  onClick={() => setCloseOpen((value) => !value)}
                  disabled={closing}
                >
                  {t('admin.shared.close')}
                </button>
              )}
            </div>

            <div className="admin-shared__meta">
              <span className="admin-shared__chat-title">
                {selected.chatTitle || t('admin.shared.untitledChat')}
              </span>
              <span>{t('admin.shared.createdAt', { date: formatDateTime(selected.createdAt, lang) })}</span>
            </div>

            <div className="admin-shared__scroll">
              {closeOpen && (
                <div className="admin-shared__close-box">
                  <label className="dialog-field">
                    <span className="dialog-field__label">{t('admin.shared.closeNoteLabel')}</span>
                    <input
                      className="dialog-input"
                      type="text"
                      value={closeNote}
                      maxLength={2000}
                      placeholder={t('admin.shared.closeNotePlaceholder')}
                      onChange={(e) => setCloseNote(e.target.value)}
                      disabled={closing}
                    />
                  </label>
                  <div className="admin-shared__close-actions">
                    <button
                      className="dialog-btn"
                      type="button"
                      onClick={() => setCloseOpen(false)}
                      disabled={closing}
                    >
                      {t('common.cancel')}
                    </button>
                    <button
                      className="dialog-btn dialog-btn--primary"
                      type="button"
                      onClick={() => void handleClose()}
                      disabled={closing}
                    >
                      {closing ? t('admin.shared.closing') : t('admin.shared.closeConfirm')}
                    </button>
                  </div>
                </div>
              )}

              {selected.comment && (
                <div className="admin-shared__comment">
                  <span className="admin-shared__comment-label">{t('admin.shared.comment')}</span>
                  {selected.comment}
                </div>
              )}

              {selected.adminNote && (
                <div className="admin-shared__comment">
                  <span className="admin-shared__comment-label">{t('admin.shared.adminNote')}</span>
                  {selected.adminNote}
                </div>
              )}

              {screenshotSrc && (
                <div className="admin-shared__screenshot">
                  <span className="admin-shared__section-title">{t('admin.shared.screenshot')}</span>
                  <a href={screenshotSrc} target="_blank" rel="noreferrer noopener">
                    <img src={screenshotSrc} alt={t('admin.shared.screenshotAlt')} />
                  </a>
                </div>
              )}

              <div className="admin-shared__section-title">{t('admin.shared.messages')}</div>
              {selectedMessages.length === 0 ? (
                <p className="muted">{t('admin.shared.noMessages')}</p>
              ) : (
                selectedMessages.map((message, index) => (
                  <div key={index} className={`admin-shared__msg admin-shared__msg--${message.role}`}>
                    <div className="admin-shared__msg-head">
                      <span className="admin-shared__msg-role">
                        {message.role === 'user' ? t('admin.shared.roleUser') : t('admin.shared.roleAssistant')}
                      </span>
                      <span className="admin-shared__event-time">
                        {formatDateTime(message.createdAt, lang)}
                      </span>
                    </div>
                    <Markdown text={message.content} />
                  </div>
                ))
              )}

              <div className="admin-shared__section-title">{t('admin.shared.agentEvents')}</div>
              {sortedEvents.length === 0 ? (
                <p className="muted">{t('admin.shared.noAgentEvents')}</p>
              ) : (
                <div className="admin-shared__events">
                  {sortedEvents.map((event, index) => (
                    <AgentEventRow key={`${event.seq}-${index}`} event={event} lang={lang} />
                  ))}
                </div>
              )}
            </div>
          </>
        ) : null}
      </div>
    </div>
  );
}
