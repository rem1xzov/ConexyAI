import { useTranslation } from 'react-i18next';
import { ChevronDownIcon } from './Icons';

// SCROLL_TO_BOTTOM: добавлено 2026-09-22
/**
 * Round "jump to the latest message" button that floats just above the composer, centered on the
 * chat column. Shared by every mode (flash / pro / coder) because it lives in `ChatFeed` rather
 * than in any one of them.
 *
 * It is purely presentational: visibility and scrolling are owned by `ChatFeed`, which is also
 * what tracks whether the user is still following the bottom of the feed.
 */
interface ScrollToBottomButtonProps {
  visible: boolean;
  onClick: () => void;
}

export function ScrollToBottomButton({ visible, onClick }: ScrollToBottomButtonProps) {
  const { t } = useTranslation();

  return (
    <button
      type="button"
      className={`scroll-to-bottom ${visible ? 'scroll-to-bottom--visible' : ''}`}
      onClick={onClick}
      // Hidden visually AND from the accessibility tree while the user is at the bottom, so it
      // never steals a tab stop or a click in the normal reading position.
      aria-hidden={!visible}
      tabIndex={visible ? 0 : -1}
      title={t('chat.scrollToBottom')}
      aria-label={t('chat.scrollToBottom')}
    >
      <ChevronDownIcon size={18} />
    </button>
  );
}
