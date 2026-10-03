import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { getSharedChat } from '../api/conexyApi';
import type { SharedChat } from '../types/api';
import { Markdown } from './Markdown';
import { ConexyLogo } from './ConexyLogo';

// SHARE_PUBLIC: добавлено 2026-10-01.
//
// Публичная страница чата: открывается по ссылке #/shared/<token> БЕЗ входа в аккаунт и показывает
// только переписку — ни рабочей области, ни инструментов, ни ввода. Это единственный публичный
// маршрут, который отдаёт содержимое чата, поэтому он намеренно не имеет ничего общего с основным
// приложением: ни сайдбара, ни композера.
interface SharedChatPageProps {
  token: string;
  /** Вернуться в приложение (войти/продолжить в своём аккаунте). */
  onOpenApp: () => void;
}

type LoadState = 'loading' | 'ready' | 'notfound' | 'error';

export function SharedChatPage({ token, onOpenApp }: SharedChatPageProps) {
  const { t } = useTranslation();
  const [chat, setChat] = useState<SharedChat | null>(null);
  const [state, setState] = useState<LoadState>('loading');

  useEffect(() => {
    let cancelled = false;
    setState('loading');
    getSharedChat(token)
      .then((data) => {
        if (cancelled) return;
        setChat(data);
        setState('ready');
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        const status = (e as { response?: { status?: number } })?.response?.status;
        setState(status === 404 ? 'notfound' : 'error');
      });
    return () => {
      cancelled = true;
    };
  }, [token]);

  return (
    <div className="shared-page">
      <header className="shared-page__head">
        <div className="shared-page__brand">
          <ConexyLogo size={24} />
          <span className="shared-page__name">{t('share.brand')}</span>
        </div>
        <button className="dialog-btn dialog-btn--primary" type="button" onClick={onOpenApp}>
          {t('share.openApp')}
        </button>
      </header>

      {state === 'loading' && <p className="shared-page__status">{t('common.loading')}</p>}
      {state === 'notfound' && <p className="shared-page__status">{t('share.notFound')}</p>}
      {state === 'error' && <p className="shared-page__status">{t('share.error')}</p>}

      {state === 'ready' && chat && (
        <div className="shared-page__body">
          <div className="shared-page__title-row">
            <h1 className="shared-page__title">{chat.title || t('share.untitled')}</h1>
            <span className="shared-page__badge">{t('share.readOnly')}</span>
          </div>

          <div className="shared-page__messages">
            {chat.messages.map((m, index) => (
              <div key={index} className={`shared-msg shared-msg--${m.role === 'user' ? 'user' : 'assistant'}`}>
                <div className="shared-msg__role">{m.role === 'user' ? t('message.you') : 'Conexy'}</div>
                {m.role === 'assistant' ? (
                  <Markdown text={m.content} />
                ) : (
                  <div className="shared-msg__text">{m.content}</div>
                )}
              </div>
            ))}
          </div>

          <p className="shared-page__disclaimer">{t('message.disclaimer')}</p>
        </div>
      )}
    </div>
  );
}
