import type { ReactNode } from 'react';

// CLAUDE_LAYOUT: добавлено 2026-09-20
export interface ChatQuickChip {
  key: string;
  label: string;
  onClick: () => void;
  active?: boolean;
}

interface ChatLayoutProps {
  /** No messages yet: the hero and the composer sit in the vertical centre. */
  empty: boolean;
  /** Start-screen content (mark + greeting), shown centred above the composer. */
  hero?: ReactNode;
  chips?: ChatQuickChip[];
  feed: ReactNode;
  composer: ReactNode;
}

/**
 * Chat stage (Claude-style start screen).
 *
 * Layout is a single flex column — [feed][hero][composer][tail] — so nothing is remounted when
 * the first message is sent. `flex-grow` on the hero/tail spacers is what positions the
 * composer, and flex-grow is animatable, so the composer travels from the centre to the bottom
 * while the start screen fades out instead of jumping.
 *
 * When empty: feed grows to 0, hero and tail grow to 1 (equal) — the composer lands dead centre,
 * with the hero just above it and the chips below. When active: the feed takes the free space and
 * the spacers collapse, leaving the composer at the bottom.
 */
export function ChatLayout({ empty, hero, chips = [], feed, composer }: ChatLayoutProps) {
  return (
    <div className={`chat-stage ${empty ? 'chat-stage--empty' : 'chat-stage--active'}`}>
      <div className="chat-stage__feed">{empty ? null : feed}</div>

      <div className="chat-stage__hero">
        {/* Wrapped so the collapse animation can cap the content without capping the spacer,
            which must stay free to grow for the centring to be exact. */}
        <div className="chat-stage__hero-inner">{hero}</div>
      </div>

      <div className="chat-stage__composer">
        {composer}
        {chips.length > 0 && (
          <div className="chat-stage__chips">
            {chips.map((chip) => (
              <button
                key={chip.key}
                type="button"
                className={`chat-chip ${chip.active ? 'chat-chip--on' : ''}`}
                onClick={chip.onClick}
                aria-pressed={chip.active}
              >
                {chip.label}
              </button>
            ))}
          </div>
        )}
      </div>

      <div className="chat-stage__tail" />
    </div>
  );
}
