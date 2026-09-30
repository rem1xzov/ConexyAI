import { memo, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { ChatSession } from '../types/chat';
import { useArtifactSelector, toggleArtifactList } from './artifacts/store';
import { ConfirmDialog, PromptDialog } from './Dialog';
import { EditIcon, FileIcon, PinIcon, PlusIcon, ShareIcon, ThreeDotsIcon, TrashIcon } from './Icons';

// CHAT_ACTIONS: добавлено 2026-09-29 — действия шапки чата (артефакты, новый чат, меню чата).
// Показываются, только когда в чате есть хотя бы одно сообщение; по умолчанию их нет. Меню
// повторяет то, что уже есть у чата в боковой панели: поделиться, закрепить, переименовать, удалить.
interface ChatHeaderActionsProps {
  session: ChatSession;
  onNewChat: () => void;
  onShare: (session: ChatSession) => void;
  onPin: (id: string) => void;
  onRename: (id: string, title: string) => void;
  onDelete: (id: string) => void;
}

export const ChatHeaderActions = memo(function ChatHeaderActions({
  session,
  onNewChat,
  onShare,
  onPin,
  onRename,
  onDelete,
}: ChatHeaderActionsProps) {
  const { t } = useTranslation();
  const [menuOpen, setMenuOpen] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [deleting, setDeleting] = useState(false);
  // ARTIFACT_LIST: кнопка «файлы» подсвечена, когда открыт список артефактов.
  const listOpen = useArtifactSelector((s) => s.listOpen);
  const moreRef = useRef<HTMLDivElement>(null);

  // Click/tap anywhere outside the menu closes it.
  useEffect(() => {
    if (!menuOpen) return;
    const onPointerDown = (e: PointerEvent) => {
      if (moreRef.current && !moreRef.current.contains(e.target as Node)) setMenuOpen(false);
    };
    document.addEventListener('pointerdown', onPointerDown);
    return () => document.removeEventListener('pointerdown', onPointerDown);
  }, [menuOpen]);

  return (
    <div className="chat-actions">
      <button
        type="button"
        className={`chat-actions__btn ${listOpen ? 'chat-actions__btn--on' : ''}`}
        onClick={() => toggleArtifactList()}
        title={t('render.artifactsTitle')}
        aria-label={t('render.artifactsTitle')}
        aria-pressed={listOpen}
      >
        <FileIcon size={18} />
      </button>

      <button
        type="button"
        className="chat-actions__btn"
        onClick={onNewChat}
        title={t('chat.newChat')}
        aria-label={t('chat.newChat')}
      >
        <PlusIcon size={18} />
      </button>

      <div className="chat-actions__more" ref={moreRef}>
        <button
          type="button"
          className="chat-actions__btn"
          onClick={() => setMenuOpen((o) => !o)}
          title={t('sidebar.sessionMenu')}
          aria-label={t('sidebar.sessionMenu')}
          aria-haspopup="menu"
          aria-expanded={menuOpen}
        >
          <ThreeDotsIcon size={18} />
        </button>

        {menuOpen && (
          <div className="chats__menu chat-actions__menu" role="menu">
            <button
              type="button"
              role="menuitem"
              className="chats__menu-item"
              onClick={() => {
                setMenuOpen(false);
                onShare(session);
              }}
            >
              <ShareIcon size={16} className="chats__menu-icon" /> {t('sidebar.share')}
            </button>
            <button
              type="button"
              role="menuitem"
              className="chats__menu-item"
              onClick={() => {
                setMenuOpen(false);
                onPin(session.id);
              }}
            >
              <PinIcon size={16} className="chats__menu-icon" />{' '}
              {session.isPinned ? t('sidebar.unpin') : t('sidebar.pin')}
            </button>
            <button
              type="button"
              role="menuitem"
              className="chats__menu-item"
              onClick={() => {
                setMenuOpen(false);
                setRenaming(true);
              }}
            >
              <EditIcon size={16} className="chats__menu-icon" /> {t('sidebar.rename')}
            </button>
            <button
              type="button"
              role="menuitem"
              className="chats__menu-item chats__menu-item--danger"
              onClick={() => {
                setMenuOpen(false);
                setDeleting(true);
              }}
            >
              <TrashIcon size={16} className="chats__menu-icon" /> {t('sidebar.delete')}
            </button>
          </div>
        )}
      </div>

      {renaming && (
        <PromptDialog
          title={t('sidebar.rename')}
          placeholder={t('sidebar.rename')}
          initialValue={session.title}
          onSubmit={(value) => {
            setRenaming(false);
            onRename(session.id, value);
          }}
          onCancel={() => setRenaming(false)}
        />
      )}

      {deleting && (
        <ConfirmDialog
          title={t('sidebar.deleteConfirmTitle')}
          message={t(
            (session.kind ?? 'chat') === 'projects'
              ? 'sidebar.deleteConfirmMessageWorkspace'
              : 'sidebar.deleteConfirmMessage',
            { title: session.title },
          )}
          confirmLabel={t('sidebar.delete')}
          danger
          onConfirm={() => {
            setDeleting(false);
            onDelete(session.id);
          }}
          onCancel={() => setDeleting(false)}
        />
      )}
    </div>
  );
});
