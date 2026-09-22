import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  ChevronDownIcon,
  EditIcon,
  FileIcon,
  FolderIcon,
  SearchIcon,
  TerminalIcon,
  GlobeIcon,
} from './Icons';

// AGENT_FEED_ZED: добавлено 2026-09-23
/**
 * One agent action as a single dense line: status icon on the left, short description, an
 * optional clickable file path, the result summary on the right.
 *
 * This is the Zed-style workhorse of the feed. Deliberately *not* a card: search, file reads and
 * file edits carry no border and no background, so a run of thirty of them reads as a timeline
 * instead of a column of identical boxes. Only a command that needs the user's decision gets a
 * real block (see `CommandConfirmCard`), because only there is there something worth hiding and
 * showing.
 */
export type ActionKind = 'search' | 'read_file' | 'edit_file' | 'list' | 'web' | 'command';

export type ActionState = 'running' | 'completed' | 'failed' | 'rejected';

interface ActionStepLineProps {
  kind: ActionKind;
  /** Verb/description shown before the path, e.g. "Читаю" or "Поиск:". */
  label: string;
  /** File the action touched — rendered as a link when `onOpenPath` can open it. */
  path?: string;
  /** Opens the path in the workspace editor; without it the path stays plain (but still visible). */
  onOpenPath?: (path: string) => void;
  state: ActionState;
  /** Muted result summary shown at the right (exit code, "готово" etc.). */
  meta?: string;
  /** Error text shown at the right instead of `meta`. */
  errorLine?: string;
  /** Output body — when present the line gains a chevron that reveals it in place. */
  detail?: string;
}

const ICON_SIZE = 13;

function iconFor(kind: ActionKind) {
  switch (kind) {
    case 'search':
      return <SearchIcon size={ICON_SIZE} />;
    case 'read_file':
      return <FileIcon size={ICON_SIZE} />;
    case 'edit_file':
      return <EditIcon size={ICON_SIZE} />;
    case 'list':
      return <FolderIcon size={ICON_SIZE} />;
    case 'web':
      return <GlobeIcon size={ICON_SIZE} />;
    case 'command':
    default:
      return <TerminalIcon size={ICON_SIZE} />;
  }
}

function statusMark(state: ActionState) {
  switch (state) {
    case 'completed':
      return <span className="action-line__mark chat-ok">✓</span>;
    case 'rejected':
      return <span className="action-line__mark chat-danger">⊘</span>;
    case 'failed':
      return <span className="action-line__mark chat-danger">✕</span>;
    default:
      return <span className="action-line__spinner" aria-hidden="true" />;
  }
}

export function ActionStepLine({
  kind,
  label,
  path,
  onOpenPath,
  state,
  meta,
  errorLine,
  detail,
}: ActionStepLineProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const expandable = Boolean(detail);

  return (
    <div className={`action-line action-line--${state}`}>
      <div className="action-line__head">
        <span className={`action-line__icon action-line__icon--${kind}`} aria-hidden="true">
          {iconFor(kind)}
        </span>

        <span className="action-line__text">
          <span className="action-line__label">{label}</span>
          {path &&
            (onOpenPath ? (
              <button
                type="button"
                className="action-line__path action-line__path--link"
                onClick={() => onOpenPath(path)}
                title={path}
              >
                {path}
              </button>
            ) : (
              <span className="action-line__path" title={path}>
                {path}
              </span>
            ))}
          {!path && errorLine && <span className="action-line__error">{errorLine}</span>}
        </span>

        {meta && !errorLine && <span className="action-line__meta">{meta}</span>}

        {statusMark(state)}

        {expandable && (
          <button
            type="button"
            className="action-line__toggle"
            onClick={() => setOpen((v) => !v)}
            aria-expanded={open}
            aria-label={t('timeline.toggleOutput')}
          >
            <ChevronDownIcon
              size={12}
              className={`action-line__chevron ${open ? 'action-line__chevron--open' : ''}`}
            />
          </button>
        )}
      </div>

      {expandable && open && <pre className="action-line__body">{detail}</pre>}
    </div>
  );
}
