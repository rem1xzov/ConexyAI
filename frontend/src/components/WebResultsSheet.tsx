import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { shortDate, type WebResult } from '../utils/webResults';

// DEEPSEEK_SEARCH_UI: шторка «Результаты поиска» — выезжает снизу, закрывается крестиком, тапом
// по затемнению или свайпом шапки вниз.

const CLOSE_MS = 240;
const DRAG_CLOSE_PX = 90;

function Favicon({ host }: { host: string }) {
  const [failed, setFailed] = useState(false);
  if (failed || !host) {
    return (
      <span className="web-sheet__favicon web-sheet__favicon--empty" aria-hidden="true">
        🌐
      </span>
    );
  }
  return (
    <img
      className="web-sheet__favicon"
      src={`https://www.google.com/s2/favicons?domain=${encodeURIComponent(host)}&sz=64`}
      alt=""
      loading="lazy"
      onError={() => setFailed(true)}
    />
  );
}

interface WebResultsSheetProps {
  results: WebResult[];
  onClose: () => void;
}

export function WebResultsSheet({ results, onClose }: WebResultsSheetProps) {
  const { t } = useTranslation();
  const [shown, setShown] = useState(false);
  const [closing, setClosing] = useState(false);
  const [dragY, setDragY] = useState(0);
  const dragStart = useRef<number | null>(null);

  useEffect(() => {
    const frame = window.requestAnimationFrame(() => setShown(true));
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close();
    };
    window.addEventListener('keydown', onKey);
    return () => {
      window.cancelAnimationFrame(frame);
      window.removeEventListener('keydown', onKey);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function close() {
    if (closing) return;
    setClosing(true);
    setDragY(0);
    window.setTimeout(onClose, CLOSE_MS);
  }

  const open = shown && !closing;
  const panelStyle = dragY > 0 ? { transform: `translate3d(0, ${dragY}px, 0)`, transition: 'none' } : undefined;

  return createPortal(
    <div className={`web-sheet ${open ? 'web-sheet--open' : ''}`} role="dialog" aria-modal="true" aria-label={t('webSheet.title')}>
      <div className="web-sheet__backdrop" onClick={close} />
      <div className="web-sheet__panel" style={panelStyle}>
        <div
          className="web-sheet__header"
          onTouchStart={(e) => {
            dragStart.current = e.touches[0]?.clientY ?? null;
          }}
          onTouchMove={(e) => {
            if (dragStart.current === null) return;
            setDragY(Math.max(0, (e.touches[0]?.clientY ?? 0) - dragStart.current));
          }}
          onTouchEnd={() => {
            dragStart.current = null;
            if (dragY > DRAG_CLOSE_PX) close();
            else setDragY(0);
          }}
        >
          <span className="web-sheet__grip" aria-hidden="true" />
          <span className="web-sheet__title">{t('webSheet.title')}</span>
          <button type="button" className="web-sheet__close" onClick={close} aria-label={t('webSheet.close')}>
            ✕
          </button>
        </div>
        <div className="web-sheet__list" data-no-drawer-swipe="true">
          {results.map((result, index) => (
            <a
              key={result.url}
              className="web-sheet__item"
              href={result.url}
              target="_blank"
              rel="noopener noreferrer"
            >
              <div className="web-sheet__meta">
                <Favicon host={result.host} />
                <span className="web-sheet__site">{result.host}</span>
                {shortDate(result.date) && <span className="web-sheet__date">{shortDate(result.date)}</span>}
                <span className="web-sheet__index">{index + 1}</span>
              </div>
              <div className="web-sheet__item-title">{result.title || result.host}</div>
              {result.snippet && <div className="web-sheet__snippet">{result.snippet}</div>}
            </a>
          ))}
        </div>
      </div>
    </div>,
    document.body,
  );
}
