import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { deleteUser, getAdminUsers, makeAdmin, revokeAdmin } from '../api/conexyApi';
import type { AdminUser } from '../types/api';
import { humanError } from '../utils/humanError';
import { ConfirmDialog } from './Dialog';
import { ShieldIcon } from './Icons';
import { AdminSupport } from './AdminSupport';

interface AdminPanelProps {
  onBack: () => void;
  onToast: (message: string) => void;
}

const PAGE_SIZE = 20;

function formatDate(iso: string, lang: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleDateString(lang === 'en' ? 'en-US' : 'ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  });
}

function roleClass(u: AdminUser): string {
  if (u.isSuperAdmin) return 'admin-user__role--super';
  if (u.isAdmin) return 'admin-user__role--admin';
  return 'admin-user__role--user';
}

// ADMIN_PANEL: добавлено 2026-09-19
/** Full-page admin panel: paginated user list with promote/demote/delete actions. */
export function AdminPanel({ onBack, onToast }: AdminPanelProps) {
  const { t, i18n } = useTranslation();
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  // SUPPORT: добавлено 2026-09-19
  const [tab, setTab] = useState<'users' | 'support'>('users');

  function roleLabel(u: AdminUser): string {
    if (u.isSuperAdmin) return t('admin.roles.super');
    if (u.isAdmin) return t('admin.roles.admin');
    return t('admin.roles.user');
  }

  // ADMIN_PANEL_FIX: добавлено 2026-09-23 — ошибка загрузки должна быть и понятной, и
  // преодолимой: 403 означает «не админ» (а не сломанные данные), а кнопка «Повторить» убирает
  // необходимость перезагружать страницу, если запрос упал по сети.
  async function load(p: number) {
    setLoading(true);
    setError(null);
    try {
      const res = await getAdminUsers(p, PAGE_SIZE);
      setUsers(res.users);
      setTotal(res.totalCount);
      setPage(res.page);
    } catch (e) {
      const status = (e as { response?: { status?: number } })?.response?.status;
      setError(status === 403 ? t('admin.noAccess') : humanError(e, t));
    } finally {
      setLoading(false);
    }
  }

  function retry() {
    void load(page);
  }

  useEffect(() => {
    void load(1);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function handleMakeAdmin(u: AdminUser) {
    setBusyId(u.id);
    try {
      await makeAdmin(u.id);
      await load(page);
      onToast(t('admin.toastMadeAdmin'));
    } catch {
      onToast(t('admin.toastFailed'));
    } finally {
      setBusyId(null);
    }
  }

  async function handleRevokeAdmin(u: AdminUser) {
    setBusyId(u.id);
    try {
      await revokeAdmin(u.id);
      await load(page);
      onToast(t('admin.toastRevokedAdmin'));
    } catch {
      onToast(t('admin.toastFailed'));
    } finally {
      setBusyId(null);
    }
  }

  async function handleDelete(u: AdminUser) {
    setBusyId(u.id);
    try {
      await deleteUser(u.id);
      await load(page);
      onToast(t('admin.toastDeleted'));
    } catch {
      onToast(t('admin.toastFailed'));
    } finally {
      setBusyId(null);
      setConfirmDeleteId(null);
    }
  }

  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  return (
    <div className="admin">
      <div className="admin__topbar">
        <button className="admin-btn" onClick={onBack} type="button">
          {t('common.back')}
        </button>
        <h1 className="admin__title">{t('admin.title')}</h1>
        <div className="admin__tabs">
          <button
            className={`admin-tab ${tab === 'users' ? 'admin-tab--active' : ''}`}
            onClick={() => setTab('users')}
            type="button"
          >
            {t('admin.usersTab')}
          </button>
          <button
            className={`admin-tab ${tab === 'support' ? 'admin-tab--active' : ''}`}
            onClick={() => setTab('support')}
            type="button"
          >
            {t('admin.supportTab')}
          </button>
        </div>
        <span className="admin__count">
          {tab === 'users' ? t('admin.userCount', { count: total }) : ''}
        </span>
      </div>

      {tab === 'support' ? (
        <AdminSupport onToast={onToast} />
      ) : (
        <>
      {loading ? (
        <p className="muted admin__empty">{t('common.loading')}</p>
      ) : error ? (
        <div className="admin__error-block">
          <p className="admin__error">{error}</p>
          <button className="admin-btn" type="button" onClick={retry}>
            {t('admin.retry')}
          </button>
        </div>
      ) : (
        <div className="admin__list">
          {users.map((u) => (
            <div key={u.id} className="admin-user">
              <div className="admin-user__info">
                <div className="admin-user__name">{u.gitHubUsername ?? u.email ?? t('account.user')}</div>
                <div className="admin-user__meta">
                  <span className="admin-user__tier">{u.tier}</span>
                  <span className={`admin-user__role ${roleClass(u)}`}>{roleLabel(u)}</span>
                  <span className="admin-user__date">{t('admin.since', { date: formatDate(u.createdAt, i18n.language) })}</span>
                </div>
              </div>

              {u.isSuperAdmin ? (
                <span className="admin-user__protected" title={t('admin.protected')}>
                  <ShieldIcon size={16} /> {t('admin.protected')}
                </span>
              ) : (
                <div className="admin-user__actions">
                  {u.isAdmin ? (
                    <button className="admin-btn" onClick={() => void handleRevokeAdmin(u)} disabled={busyId === u.id} type="button">
                      {t('admin.revokeAdmin')}
                    </button>
                  ) : (
                    <button className="admin-btn" onClick={() => void handleMakeAdmin(u)} disabled={busyId === u.id} type="button">
                      {t('admin.makeAdmin')}
                    </button>
                  )}
                  <button
                    className="admin-btn admin-btn--danger"
                    onClick={() => setConfirmDeleteId(u.id)}
                    disabled={busyId === u.id}
                    type="button"
                  >
                    {t('admin.delete')}
                  </button>
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      <div className="admin__pagination">
        <button className="admin-btn" disabled={page <= 1 || loading} onClick={() => void load(page - 1)} type="button">
          {t('admin.prev')}
        </button>
        <span className="admin__page">
          {page} / {totalPages}
        </span>
        <button
          className="admin-btn"
          disabled={page >= totalPages || loading}
          onClick={() => void load(page + 1)}
          type="button"
        >
          {t('admin.next')}
        </button>
      </div>
        </>
      )}

      {confirmDeleteId && (
        <ConfirmDialog
          title={t('admin.deleteConfirmTitle')}
          message={t('admin.deleteConfirmMessage')}
          confirmLabel={t('admin.confirmDelete')}
          danger
          onConfirm={() => {
            const u = users.find((x) => x.id === confirmDeleteId);
            if (u) void handleDelete(u);
          }}
          onCancel={() => setConfirmDeleteId(null)}
        />
      )}
    </div>
  );
}
