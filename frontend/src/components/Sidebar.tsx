import { useEffect, useRef, useState } from 'react';
import type { ChatSession, ChatSessionKind } from '../types/chat';
import type { UserProfile } from '../types/api';
import { AccountWidget } from './AccountWidget';
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
  onToast: (message: string) => void;
}

const TABS: { key: ChatSessionKind; label: string }[] = [
  { key: 'chat', label: 'Chat' },
  { key: 'projects', label: 'Agent' },
  { key: 'students', label: 'Students' },
];

export function Sidebar(props: SidebarProps) {
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
    onToast,
  } = props;

  const [openMenuId, setOpenMenuId] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editValue, setEditValue] = useState('');
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
    .sort((a, b) => Number(b.isPinned ?? false) - Number(a.isPinned ?? false));

  const newLabel =
    activeTab === 'students' ? 'New Student Session' : activeTab === 'projects' ? 'New Agent Task' : 'New Chat';

  const listLabel = activeTab === 'projects' ? 'Agent' : activeTab === 'students' ? 'Students' : 'Chats and tasks';

  function startRename(s: ChatSession) {
    setEditingId(s.id);
    setEditValue(s.title);
    setOpenMenuId(null);
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
    <aside className={`sidebar ${open ? '' : 'sidebar--collapsed'}`}>
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
              placeholder="Search chats"
            />
          </div>

          <div className="tabs">
            {TABS.map((t) => (
              <button
                key={t.key}
                className={`tabs__btn ${activeTab === t.key ? 'tabs__btn--active' : ''}`}
                onClick={() => onTabChange(t.key)}
              >
                {t.label}
              </button>
            ))}
          </div>

          <button className="new-chat" onClick={() => onNewSession(activeTab)}>
            <PlusIcon size={16} /> {newLabel}
          </button>

          <div className="chats">
            <div className="chats__label">{listLabel}</div>
            {filtered.length === 0 ? (
              <p className="muted chats__empty">No sessions yet</p>
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
                      onClick={() => setOpenMenuId((prev) => (prev === s.id ? null : s.id))}
                      aria-label="Session menu"
                    >
                      <ThreeDotsIcon size={16} />
                    </button>

                    {openMenuId === s.id && (
                      <div className="chats__menu" ref={menuRef}>
                        <button
                          className="chats__menu-item"
                          onClick={() => {
                            onShareSession(s);
                            setOpenMenuId(null);
                          }}
                        >
                          <ShareIcon size={16} className="chats__menu-icon" /> Share conversation
                        </button>
                        <button
                          className="chats__menu-item"
                          onClick={() => {
                            onPinSession(s.id);
                            setOpenMenuId(null);
                          }}
                        >
                          <PinIcon size={16} className="chats__menu-icon" /> {s.isPinned ? 'Unpin' : 'Pin'}
                        </button>
                        <button className="chats__menu-item" onClick={() => startRename(s)}>
                          <EditIcon size={16} className="chats__menu-icon" /> Rename
                        </button>
                        <button
                          className="chats__menu-item chats__menu-item--danger"
                          onClick={() => {
                            onDeleteSession(s.id);
                            setOpenMenuId(null);
                          }}
                        >
                          <TrashIcon size={16} className="chats__menu-icon" /> Delete
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
              <AccountWidget user={user} onLogout={onLogout} onToast={onToast} />
            ) : (
              <div className="sidebar__auth">
                <button className="sidebar__auth-btn" onClick={onLogin} type="button">
                  Войти
                </button>
                <button
                  className="sidebar__auth-btn sidebar__auth-btn--primary"
                  onClick={onRegister}
                  type="button"
                >
                  Регистрация
                </button>
              </div>
            )}
          </div>
        </>
      )}
    </aside>
  );
}
