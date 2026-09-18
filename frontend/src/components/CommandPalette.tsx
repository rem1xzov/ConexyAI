import { useEffect, useMemo, useRef, useState } from 'react';
import type { KeyboardEvent as ReactKeyboardEvent } from 'react';
import { FileTypeIcon } from './FileTypeIcon';
import { PlayIcon, TerminalIcon, CheckIcon, CodeIcon } from './Icons';

interface CommandPaletteProps {
  open: boolean;
  files: string[];
  onClose: () => void;
  onOpenFile: (path: string) => void;
  onRun: () => void;
  onOpenTerminal: () => void;
  onToggleTodo: () => void;
  onSave: () => void;
}

interface Item {
  key: string;
  kind: 'command' | 'file';
  label: string;
  path?: string;
  action: () => void;
}

function fuzzyMatch(query: string, target: string): number[] | null {
  const q = query.toLowerCase();
  const t = target.toLowerCase();
  const indices: number[] = [];
  let ti = 0;
  for (let qi = 0; qi < q.length; qi++) {
    const c = q[qi];
    let found = -1;
    for (let k = ti; k < t.length; k++) {
      if (t[k] === c) {
        found = k;
        break;
      }
    }
    if (found === -1) return null;
    indices.push(found);
    ti = found + 1;
  }
  return indices;
}

function Highlight({ text, indices }: { text: string; indices: number[] }) {
  if (indices.length === 0) return <>{text}</>;
  const set = new Set(indices);
  return (
    <>
      {text.split('').map((ch, i) =>
        set.has(i) ? (
          <mark key={i} className="palette__match">
            {ch}
          </mark>
        ) : (
          <span key={i}>{ch}</span>
        ),
      )}
    </>
  );
}

export function CommandPalette({
  open,
  files,
  onClose,
  onOpenFile,
  onRun,
  onOpenTerminal,
  onToggleTodo,
  onSave,
}: CommandPaletteProps) {
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (open) {
      setQuery('');
      setSelected(0);
      requestAnimationFrame(() => inputRef.current?.focus());
    }
  }, [open]);

  const items = useMemo<Item[]>(() => {
    const commands: Item[] = [
      { key: 'cmd:run', kind: 'command', label: 'Запустить проект', action: onRun },
      { key: 'cmd:terminal', kind: 'command', label: 'Открыть терминал', action: onOpenTerminal },
      { key: 'cmd:todo', kind: 'command', label: 'Переключить todo-панель', action: onToggleTodo },
      { key: 'cmd:save', kind: 'command', label: 'Сохранить файл', action: onSave },
    ];

    const q = query.trim();
    if (!q) return commands;

    const matchedCommands = commands.filter((c) => fuzzyMatch(q, c.label) !== null);

    const fileResults: { path: string; indices: number[]; score: number }[] = [];
    for (const f of files) {
      const indices = fuzzyMatch(q, f);
      if (indices === null) continue;
      const name = f.split('/').pop() ?? f;
      const exact = name.toLowerCase().includes(q.toLowerCase()) || f.toLowerCase().includes(q.toLowerCase());
      const score = (exact ? 0 : 100) + f.length;
      fileResults.push({ path: f, indices, score });
    }
    fileResults.sort((a, b) => a.score - b.score);

    const fileItems: Item[] = fileResults.slice(0, 50).map((r) => ({
      key: `file:${r.path}`,
      kind: 'file',
      label: r.path,
      path: r.path,
      action: () => onOpenFile(r.path),
    }));

    return [...matchedCommands, ...fileItems];
  }, [query, files, onRun, onOpenTerminal, onToggleTodo, onSave, onOpenFile]);

  useEffect(() => {
    setSelected(0);
    listRef.current?.scrollTo({ top: 0 });
  }, [query]);

  if (!open) return null;

  function activate(item: Item) {
    onClose();
    item.action();
  }

  function onKeyDown(e: ReactKeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Escape') {
      onClose();
    } else if (e.key === 'ArrowDown') {
      e.preventDefault();
      setSelected((s) => Math.min(s + 1, items.length - 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setSelected((s) => Math.max(s - 1, 0));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const item = items[selected];
      if (item) activate(item);
    }
  }

  return (
    <div className="palette" onMouseDown={onClose}>
      <div className="palette__dialog" onMouseDown={(e) => e.stopPropagation()}>
        <input
          ref={inputRef}
          className="palette__input"
          placeholder="Поиск файлов и команд…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={onKeyDown}
        />
        <div className="palette__list" ref={listRef}>
          {items.length === 0 && <div className="palette__empty">Ничего не найдено</div>}
          {items.map((item, i) => {
            const indices = item.kind === 'file' ? (fuzzyMatch(query, item.label) ?? []) : (fuzzyMatch(query, item.label) ?? []);
            return (
              <button
                key={item.key}
                className={`palette__item ${i === selected ? 'palette__item--active' : ''}`}
                onMouseEnter={() => setSelected(i)}
                onClick={() => activate(item)}
              >
                <span className="palette__icon">
                  {item.kind === 'file' ? (
                    <FileTypeIcon path={item.path ?? ''} size={15} />
                  ) : item.key === 'cmd:run' ? (
                    <PlayIcon size={15} />
                  ) : item.key === 'cmd:terminal' ? (
                    <TerminalIcon size={15} />
                  ) : item.key === 'cmd:todo' ? (
                    <CheckIcon size={15} />
                  ) : (
                    <CodeIcon size={15} />
                  )}
                </span>
                <span className="palette__label">
                  <Highlight text={item.label} indices={indices} />
                </span>
                <span className="palette__kind">{item.kind === 'command' ? 'command' : 'file'}</span>
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}
