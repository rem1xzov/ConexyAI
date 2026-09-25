import { useEffect, useRef, useState, type Ref } from 'react';
import { useTranslation } from 'react-i18next';
import type { ChatSession, ChatSessionKind } from '../types/chat';
import type { UserProfile } from '../types/api';
import { AccountWidget } from './AccountWidget';
import { ConfirmDialog } from './Dialog';
import {
  MenuIcon,
  SearchIcon,
  PlusIcon,
  ThreeDotsIcon,
  ShareIcon,
  PinIcon,
  EditIcon,
  TrashIcon,
} from './Icons';

interface SidebarProps {
  open: boolean;
  activeTab: ChatSessionKind;
  search: string;
  sessions: ChatSession[];
  activeId: string | null;
  onToggle: () => void;
  onTabChange: (tab: ChatSessionKind) => void;
  onNewSession: (kind: ChatSessionKind) => void;
  onSelectSession: (id: string) => void;
  onSearchChange: (value: string) => void;
  onShareSession: (session: ChatSession) => void;
  onPinSession: (id: string) => void;
  onRenameSession: (id: string, title: string) => void;
  onDeleteSession: (id: string) => void;
  // EMAIL_AUTH: добавлено 2026-09-19
  user: UserProfile | null;
  onLogin: () => void;
  onRegister: () => void;
  onLogout: () => void;
  onUpgrade: () => void;
  onOpenAdmin: () => void;
  onOpenSupport: () => void;
  onOpenSettings: () => void;
  // MOBILE_DRAWER: the drawer swipe needs the panel node itself — the transform is written straight
  // to the element while the finger moves, without going through React state.
  asideRef?: Ref<HTMLElement>;
}

const TABS: { key: ChatSessionKind; labelKey: string }[] = [
  { key: 'chat', labelKey: 'sidebar.tabs.chat' },
  { key: 'projects', labelKey: 'sidebar.tabs.agent' },
  { key: 'students', labelKey: 'sidebar.tabs.students' },
];

// CHAT_PIN: порядок в сайдбаре — сначала закреплённые, затем по последней активности
// (последнее сообщение, а если его ещё нет — время создания чата). Без второго ключа порядок
// зависел бы от порядка массива sessions, то есть от того, в каком порядке чаты подтянулись.
function lastActiveAt(s: ChatSession): number {
  return s.messages.length > 0 ? s.messages[s.messages.length - 1].createdAt : s.createdAt;
}

export function Sidebar(props: SidebarProps) {
  const { t } = useTranslation();
  const {
    open,
    activeTab,
    search,
    sessions,
    activeId,
    onToggle,
    onTabChange,
    onNewSession,
    onSelectSession,
    onSearchChange,
    onShareSession,
    onPinSession,
    onRenameSession,
    onDeleteSession,
    user,
    onLogin,
    onRegister,
    onLogout,
    onUpgrade,
    onOpenAdmin,
    onOpenSupport,
    onOpenSettings,
    asideRef,
  } = props;

  const [openMenuId, setOpenMenuId] = useState<string | null>(null);
  // CHAT_MENU_FLIP: направление открытия меню пересчитывается при каждом клике — иначе меню
  // последнего чата в списке уезжает за нижнюю границу и обрезается боковой панелью.
  const [menuUp, setMenuUp] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editValue, setEditValue] = useState('');
  // CONFIRM_DIALOGS: добавлено 2026-09-24 — удаление чата необратимо (история на сервере + файлы
  // рабочей области), поэтому пункт меню только открывает подтверждение, а не удаляет сразу.
  const [pendingDelete, setPendingDelete] = useState<ChatSession | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    function onClickOutside(e: MouseEvent) {
      const target = e.target as HTMLElement;
      if (target.closest('.chats__menu-btn')) return;
      if (menuRef.current && !menuRef.current.contains(target)) {
        setOpenMenuId(null);
      }
    }
    document.addEventListener('mousedown', onClickOutside);
    return () => document.removeEventListener('mousedown', onClickOutside);
  }, []);

  const filtered = [...sessions]
    .filter((s) => (s.kind ?? 'chat') === activeTab)
    .filter((s) => !search.trim() || s.title.toLowerCase().includes(search.trim().toLowerCase()))
    .sort(
      (a, b) =>
        Number(b.isPinned ?? false) - Number(a.isPinned ?? false) ||
        lastActiveAt(b) - lastActiveAt(a),
    );

  const newLabel = t(
    activeTab === 'students'
      ? 'sidebar.newStudentSession'
      : activeTab === 'projects'
        ? 'sidebar.newAgentTask'
        : 'sidebar.newChat',
  );

  const listLabel = t(
    activeTab === 'projects'
      ? 'sidebar.listLabelAgent'
      : activeTab === 'students'
        ? 'sidebar.listLabelStudents'
        : 'sidebar.listLabelChat',
  );

  function startRename(s: ChatSession) {
    setEditingId(s.id);
    setEditValue(s.title);
    setOpenMenuId(null);
  }

  // CHAT_MENU_FLIP: меню открывается вниз, если под кнопкой есть место, и вверх — если его нет.
  // Высота — грубая оценка содержимого (4 пункта + отступы): точное измерение потребовало бы
  // рендера меню до решения о направлении.
  const MENU_HEIGHT_PX = 200;

  function toggleMenu(id: string, button: HTMLElement) {
    if (openMenuId === id) {
      setOpenMenuId(null);
      return;
    }

    const rect = button.getBoundingClientRect();
    const spaceBelow = window.innerHeight - rect.bottom;
    const spaceAbove = rect.top;
    // Вверх уходим только тогда, когда снизу действительно тесно, а сверху просторнее — иначе
    // меню у первого чата списка просто вылезло бы за верхнюю границу панели.
    setMenuUp(spaceBelow < MENU_HEIGHT_PX && spaceAbove > spaceBelow);
    setOpenMenuId(id);
  }

  function commitRename() {
    if (editingId) {
      const title = editValue.trim();
      if (title) onRenameSession(editingId, title);
    }
    setEditingId(null);
    setEditValue('');
  }

  return (
    <aside ref={asideRef} className={`sidebar ${open ? '' : 'sidebar--collapsed'}`}>
      <div className="sidebar__top">
        <button className="icon-btn" onClick={onToggle} title="Toggle sidebar" aria-label="Toggle sidebar">
          <MenuIcon size={20} />
        </button>
      </div>

      {open && (
        <>
          <div className="search-pill">
            <SearchIcon size={16} className="search-pill__icon" />
            <input
              value={search}
              onChange={(e) => onSearchChange(e.target.value)}
              placeholder={t('sidebar.search')}
            />
          </div>

          <div className="tabs">
            {TABS.map((tab) => (
              <button
                key={tab.key}
                className={`tabs__btn ${activeTab === tab.key ? 'tabs__btn--active' : ''}`}
                onClick={() => onTabChange(tab.key)}
              >
                {t(tab.labelKey)}
              </button>
            ))}
          </div>

          <button className="new-chat" onClick={() => onNewSession(activeTab)}>
            <PlusIcon size={16} /> {newLabel}
          </button>

          <div className="chats">
            <div className="chats__label">{listLabel}</div>
            {filtered.length === 0 ? (
              <p className="muted chats__empty">{t('sidebar.noSessions')}</p>
            ) : (
              <ul className="chats__list">
                {filtered.map((s) => (
                  <li key={s.id} className="chats__row">
                    {editingId === s.id ? (
                      <div className="chats__rename">
                        <input
                          autoFocus
                          value={editValue}
                          onChange={(e) => setEditValue(e.target.value)}
                          onKeyDown={(e) => {
                            if (e.key === 'Enter') commitRename();
                            if (e.key === 'Escape') {
                              setEditingId(null);
                              setEditValue('');
                            }
                          }}
                          onBlur={commitRename}
                        />
                      </div>
                    ) : (
                      <button
                        className={`chats__item ${s.id === activeId ? 'chats__item--active' : ''}`}
                        onClick={() => {
                          onSelectSession(s.id);
                          setOpenMenuId(null);
                        }}
                      >
                        <span className={`status-dot status-dot--${s.status.toLowerCase()}`} />
                        {s.isPinned && <PinIcon size={13} className="chats__pin" />}
                        <span className="chats__title">{s.title}</span>
                      </button>
                    )}

                    <button
                      className="chats__menu-btn"
                      onClick={(e) => toggleMenu(s.id, e.currentTarget)}
                      aria-label={t('sidebar.sessionMenu')}
                      aria-expanded={openMenuId === s.id}
                    >
                      <ThreeDotsIcon size={16} />
                    </button>

                    {openMenuId === s.id && (
                      <div className={`chats__menu ${menuUp ? 'chats__menu--up' : ''}`} ref={menuRef}>
                        <button
                          className="chats__menu-item"
                          onClick={() => {
                            onShareSession(s);
                            setOpenMenuId(null);
                          }}
                        >
                          <ShareIcon size={16} className="chats__menu-icon" /> {t('sidebar.share')}
                        </button>
                        <button
                          className="chats__menu-item"
                          onClick={() => {
                            onPinSession(s.id);
                            setOpenMenuId(null);
                          }}
                        >
                          <PinIcon size={16} className="chats__menu-icon" /> {s.isPinned ? t('sidebar.unpin') : t('sidebar.pin')}
                        </button>
                        <button className="chats__menu-item" onClick={() => startRename(s)}>
                          <EditIcon size={16} className="chats__menu-icon" /> {t('sidebar.rename')}
                        </button>
                        <button
                          className="chats__menu-item chats__menu-item--danger"
                          onClick={() => {
                            setPendingDelete(s);
                            setOpenMenuId(null);
                          }}
                        >
                          <TrashIcon size={16} className="chats__menu-icon" /> {t('sidebar.delete')}
                        </button>
                      </div>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </div>

          {/* EMAIL_AUTH: добавлено 2026-09-19 */}
          <div className="sidebar__footer">
            {user ? (
              <AccountWidget
                user={user}
                onLogout={onLogout}
                onUpgrade={onUpgrade}
                onOpenAdmin={onOpenAdmin}
                onOpenSupport={onOpenSupport}
                onOpenSettings={onOpenSettings}
              />
            ) : (
              <div className="sidebar__auth">
                <button className="sidebar__auth-btn" onClick={onLogin} type="button">
                  {t('sidebar.login')}
                </button>
                <button
                  className="sidebar__auth-btn sidebar__auth-btn--primary"
                  onClick={onRegister}
                  type="button"
                >
                  {t('sidebar.register')}
                </button>
              </div>
            )}
          </div>
        </>
      )}

      {pendingDelete && (
        <ConfirmDialog
          title={t('sidebar.deleteConfirmTitle')}
          message={t(
            (pendingDelete.kind ?? 'chat') === 'projects'
              ? 'sidebar.deleteConfirmMessageWorkspace'
              : 'sidebar.deleteConfirmMessage',
            { title: pendingDelete.title },
          )}
          confirmLabel={t('sidebar.delete')}
          danger
          onConfirm={() => {
            const id = pendingDelete.id;
            setPendingDelete(null);
            onDeleteSession(id);
          }}
          onCancel={() => setPendingDelete(null)}
        />
      )}
    </aside>
  );
}
