import { useEffect, useState } from 'react';
import { deleteUser, getAdminUsers, makeAdmin, revokeAdmin } from '../api/conexyApi';
import type { AdminUser } from '../types/api';
import { ConfirmDialog } from './Dialog';
import { ShieldIcon } from './Icons';
import { AdminSupport } from './AdminSupport';

interface AdminPanelProps {
  onBack: () => void;
  onToast: (message: string) => void;
}

const PAGE_SIZE = 20;

function formatDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric' });
}

function roleLabel(u: AdminUser): string {
  if (u.isSuperAdmin) return 'Суперадмин';
  if (u.isAdmin) return 'Админ';
  return 'Пользователь';
}

function roleClass(u: AdminUser): string {
  if (u.isSuperAdmin) return 'admin-user__role--super';
  if (u.isAdmin) return 'admin-user__role--admin';
  return 'admin-user__role--user';
}

// ADMIN_PANEL: добавлено 2026-09-19
/** Full-page admin panel: paginated user list with promote/demote/delete actions. */
export function AdminPanel({ onBack, onToast }: AdminPanelProps) {
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  // SUPPORT: добавлено 2026-09-19
  const [tab, setTab] = useState<'users' | 'support'>('users');

  async function load(p: number) {
    setLoading(true);
    setError(null);
    try {
      const res = await getAdminUsers(p, PAGE_SIZE);
      setUsers(res.users);
      setTotal(res.totalCount);
      setPage(res.page);
    } catch {
      setError('Не удалось загрузить пользователей');
    } finally {
      setLoading(false);
    }
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
      onToast('Пользователь назначен админом');
    } catch {
      onToast('Не удалось назначить админом');
    } finally {
      setBusyId(null);
    }
  }

  async function handleRevokeAdmin(u: AdminUser) {
    setBusyId(u.id);
    try {
      await revokeAdmin(u.id);
      await load(page);
      onToast('Права администратора отозваны');
    } catch {
      onToast('Не удалось отозвать права');
    } finally {
      setBusyId(null);
    }
  }

  async function handleDelete(u: AdminUser) {
    setBusyId(u.id);
    try {
      await deleteUser(u.id);
      await load(page);
      onToast('Пользователь удалён');
    } catch {
      onToast('Не удалось удалить пользователя');
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
          ← Назад
        </button>
        <h1 className="admin__title">Админ-панель</h1>
        <div className="admin__tabs">
          <button
            className={`admin-tab ${tab === 'users' ? 'admin-tab--active' : ''}`}
            onClick={() => setTab('users')}
            type="button"
          >
            Пользователи
          </button>
          <button
            className={`admin-tab ${tab === 'support' ? 'admin-tab--active' : ''}`}
            onClick={() => setTab('support')}
            type="button"
          >
            Обращения в поддержку
          </button>
        </div>
        <span className="admin__count">{tab === 'users' ? `${total} пользователей` : ''}</span>
      </div>

      {tab === 'support' ? (
        <AdminSupport onToast={onToast} />
      ) : (
        <>
      {loading ? (
        <p className="muted admin__empty">Загрузка…</p>
      ) : error ? (
        <p className="admin__error">{error}</p>
      ) : (
        <div className="admin__list">
          {users.map((u) => (
            <div key={u.id} className="admin-user">
              <div className="admin-user__info">
                <div className="admin-user__name">{u.gitHubUsername ?? u.email ?? 'Пользователь'}</div>
                <div className="admin-user__meta">
                  <span className="admin-user__tier">{u.tier}</span>
                  <span className={`admin-user__role ${roleClass(u)}`}>{roleLabel(u)}</span>
                  <span className="admin-user__date">с {formatDate(u.createdAt)}</span>
                </div>
              </div>

              {u.isSuperAdmin ? (
                <span className="admin-user__protected" title="Суперадмин защищён от изменений">
                  <ShieldIcon size={16} /> Защищён
                </span>
              ) : (
                <div className="admin-user__actions">
                  {u.isAdmin ? (
                    <button className="admin-btn" onClick={() => void handleRevokeAdmin(u)} disabled={busyId === u.id} type="button">
                      Забрать права
                    </button>
                  ) : (
                    <button className="admin-btn" onClick={() => void handleMakeAdmin(u)} disabled={busyId === u.id} type="button">
                      Сделать админом
                    </button>
                  )}
                  <button
                    className="admin-btn admin-btn--danger"
                    onClick={() => setConfirmDeleteId(u.id)}
                    disabled={busyId === u.id}
                    type="button"
                  >
                    Полностью удалить
                  </button>
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      <div className="admin__pagination">
        <button className="admin-btn" disabled={page <= 1 || loading} onClick={() => void load(page - 1)} type="button">
          Назад
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
          Вперёд
        </button>
      </div>
        </>
      )}

      {confirmDeleteId && (
        <ConfirmDialog
          title="Удалить пользователя?"
          message="Будут безвозвратно удалены все его чаты, документы и данные. Это действие нельзя отменить."
          confirmLabel="Удалить"
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
