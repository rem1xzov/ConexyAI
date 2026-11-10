import { useTranslation } from 'react-i18next';
import { GlobeIcon } from './Icons';

// CHAT_OPTIONS: the start screen used to show three chips that looked like a mode selector but
// behaved as independent toggles. Now the response style is one segmented control (radio) and
// smart search is a separate switch, so it is obvious what is selected.
type ChatMode = 'ConexyV1-flash' | 'ConexyV1-pro';

interface ChatOptionsProps {
  mode: ChatMode;
  search: boolean;
  onMode: (mode: ChatMode) => void;
  onSearch: () => void;
}

export function ChatOptions({ mode, search, onMode, onSearch }: ChatOptionsProps) {
  const { t } = useTranslation();
  const deep = mode === 'ConexyV1-pro';

  return (
    <div className="chat-options">
      <div className="chat-options__style">
        <span className="chat-options__label">{t('chat.responseStyle')}</span>
        <div className="chat-segment" role="radiogroup" aria-label={t('chat.responseStyle')}>
          <span className={`chat-segment__thumb${deep ? ' chat-segment__thumb--right' : ''}`} aria-hidden="true" />
          <button
            type="button"
            role="radio"
            aria-checked={!deep}
            className={`chat-segment__btn${deep ? '' : ' chat-segment__btn--on'}`}
            onClick={() => onMode('ConexyV1-flash')}
          >
            {t('chat.modeFast')}
          </button>
          <button
            type="button"
            role="radio"
            aria-checked={deep}
            className={`chat-segment__btn${deep ? ' chat-segment__btn--on' : ''}`}
            onClick={() => onMode('ConexyV1-pro')}
          >
            {t('chat.modeDeep')}
          </button>
        </div>
      </div>

      <button
        type="button"
        role="switch"
        aria-checked={search}
        className={`chat-switch${search ? ' chat-switch--on' : ''}`}
        onClick={onSearch}
      >
        <GlobeIcon size={15} className="chat-switch__icon" />
        <span className="chat-switch__label">{t('chat.chipSmartSearch')}</span>
        <span className="chat-switch__track" aria-hidden="true">
          <span className="chat-switch__thumb" />
        </span>
      </button>
    </div>
  );
}
