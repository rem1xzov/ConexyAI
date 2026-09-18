import { useEffect, useRef, useState } from 'react';

export interface MenuItem {
  label?: string;
  shortcut?: string;
  action?: () => void;
  disabled?: boolean;
  separator?: boolean;
}

export interface Menu {
  label: string;
  items: MenuItem[];
}

/**
 * A minimal VS Code-style top menu bar: horizontal section labels that open a
 * dropdown of actionable items (with optional shortcut hints). Purely presentational;
 * each item wires into existing WorkspacePanel handlers.
 */
export function MenuBar({ menus }: { menus: Menu[] }) {
  const [openIndex, setOpenIndex] = useState<number | null>(null);
  const rootRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    function onClick(e: MouseEvent) {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) {
        setOpenIndex(null);
      }
    }
    document.addEventListener('mousedown', onClick);
    return () => document.removeEventListener('mousedown', onClick);
  }, []);

  return (
    <div className="menubar" ref={rootRef}>
      {menus.map((menu, i) => (
        <div key={menu.label} className="menubar__group">
          <button
            className={`menubar__label ${openIndex === i ? 'menubar__label--open' : ''}`}
            onClick={() => setOpenIndex((prev) => (prev === i ? null : i))}
            onMouseEnter={() => {
              if (openIndex !== null) setOpenIndex(i);
            }}
            type="button"
          >
            {menu.label}
          </button>
          {openIndex === i && (
            <div className="menubar__menu">
              {menu.items.map((item, j) =>
                item.separator ? (
                  <div key={j} className="menubar__sep" />
                ) : (
                  <button
                    key={j}
                    className="menubar__item"
                    disabled={item.disabled}
                    onClick={() => {
                      setOpenIndex(null);
                      item.action?.();
                    }}
                    type="button"
                  >
                    <span className="menubar__item-label">{item.label}</span>
                    {item.shortcut && <span className="menubar__shortcut">{item.shortcut}</span>}
                  </button>
                ),
              )}
            </div>
          )}
        </div>
      ))}
    </div>
  );
}
