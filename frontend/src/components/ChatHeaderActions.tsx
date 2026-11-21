import { memo, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { ChatSession } from '../types/chat';
import { ConfirmDialog, PromptDialog } from './Dialog';
import { getProject, setProject } from '../utils/projects';
import { EditIcon, EyeIcon, EyeOffIcon, LightbulbIcon, PinIcon, PlusIcon, ShareIcon, ShieldIcon, ThreeDotsIcon, TrashIcon } from './Icons';
import { BottomSheet } from './BottomSheet';
import { triggerHaptic } from '../utils/haptics';

// CHAT_ACTIONS: добавлено 2026-09-29 — действия шапки чата (новый чат, меню чата). Показываются,
// только когда в чате есть хотя бы одно сообщение; по умолчанию их нет. Меню повторяет то, что уже
// есть у чата в боковой панели: поделиться, закрепить, переименовать, удалить.
interface ChatHeaderActionsProps {
  session: ChatSession;
  onNewChat: () => void;
  onShare: (session: ChatSession) => void;
  // SHARE_ADMIN: отправить чат администратору (жалоба/разбор проблемы).
  onShareAdmin: (session: ChatSession) => void;
  // GOAL: панель долгоживущей цели чата (раунды запускает сервер).
  onGoal?: (session: ChatSession) => void;
  onPin: (id: string) => void;
  // CHAT_HIDE: скрыть чат или вернуть скрытый в общий список.
  onHide: (id: string, hidden: boolean) => void;
  onRename: (id: string, title: string) => void;
  onDelete: (id: string) => void;
  // CHAT_EXPORT: выгрузка диалога в Markdown.
  onExport?: (session: ChatSession) => void;
}

export const ChatHeaderActions = memo(function ChatHeaderActions({
  session,
  onNewChat,
  onShare,
  onShareAdmin,
  onGoal,
  onPin,
  onHide,
  onRename,
  onDelete,
  onExport,
}: ChatHeaderActionsProps) {
  const { t } = useTranslation();
  const [menuOpen, setMenuOpen] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [deleting, setDeleting] = useState(false);
  // PROJECTS: раскладка чата по локальным папкам.
  const [projecting, setProjecting] = useState(false);
  const moreRef = useRef<HTMLDivElement>(null);

  // Клик по фону/свайп/Back обрабатывает BottomSheet — document-close здесь только мешал.

  return (
    <div className="chat-actions">
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
          onClick={() => {
            setMenuOpen((o) => !o);
            triggerHaptic('light');
          }}
          title={t('sidebar.sessionMenu')}
          aria-label={t('sidebar.sessionMenu')}
          aria-haspopup="menu"
          aria-expanded={menuOpen}
        >
          <ThreeDotsIcon size={18} />
        </button>

        <BottomSheet
          open={menuOpen}
          onClose={() => setMenuOpen(false)}
          title={t('sidebar.sessionMenu')}
          ariaLabel={t('sidebar.sessionMenu')}
        >
          <div className="sheet-actions" role="menu">
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
            {(session.kind ?? 'chat') === 'projects' && onExport && (
              <button
                type="button"
                role="menuitem"
                className="chats__menu-item"
                onClick={() => {
                  setMenuOpen(false);
                  onExport(session);
                }}
              >
                <span className="chats__menu-icon" aria-hidden="true">⤓</span> {t('message.exportMarkdown')}
              </button>
            )}
            {/* SHARE_ADMIN: передача чата админу с комментарием и скриншотом. */}
            <button
              type="button"
              role="menuitem"
              className="chats__menu-item"
              onClick={() => {
                setMenuOpen(false);
                onShareAdmin(session);
              }}
            >
              <ShieldIcon size={16} className="chats__menu-icon" /> {t('sidebar.shareAdmin')}
            </button>
            {/* GOAL: долгоживущая цель чата — раунды запускает сервер. */}
            {(session.kind ?? 'chat') === 'projects' && onGoal && (
              <button
                type="button"
                role="menuitem"
                className="chats__menu-item"
                onClick={() => {
                  setMenuOpen(false);
                  onGoal(session);
                }}
              >
                <LightbulbIcon size={16} className="chats__menu-icon" /> {t('goal.menu')}
              </button>
            )}
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
            {!session.incognito && (
              <button
                type="button"
                role="menuitem"
                className="chats__menu-item"
                onClick={() => {
                  setMenuOpen(false);
                  onHide(session.id, !session.isHidden);
                }}
              >
                {session.isHidden ? (
                  <EyeIcon size={16} className="chats__menu-icon" />
                ) : (
                  <EyeOffIcon size={16} className="chats__menu-icon" />
                )}{' '}
                {session.isHidden ? t('sidebar.unhide') : t('sidebar.hide')}
              </button>
            )}
            {(session.kind ?? 'chat') === 'projects' && (
              <button
                type="button"
                role="menuitem"
                className="chats__menu-item"
                onClick={() => {
                  setMenuOpen(false);
                  setProjecting(true);
                }}
              >
                <span className="chats__menu-icon" aria-hidden="true">▤</span> {t('extras.projectAssign')}
              </button>
            )}
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
        </BottomSheet>
      </div>

      {projecting && (
        <PromptDialog
          title={t('extras.projectAssign')}
          message={t('extras.projectAssignHint')}
          placeholder={t('extras.projectName')}
          initialValue={getProject(session.id)}
          onSubmit={(value) => {
            setProjecting(false);
            setProject(session.id, value);
          }}
          onCancel={() => setProjecting(false)}
        />
      )}

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
