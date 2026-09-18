import { useEffect, useRef, useState } from 'react';
import { FileTypeIcon } from './FileTypeIcon';

interface BreadcrumbsProps {
  /** Active file path relative to the workspace root, e.g. "src/Services/Foo.cs". */
  path: string;
  /** Flat list of every workspace file path (relative to root). */
  files: string[];
  onOpenFile: (path: string) => void;
}

interface Children {
  dirs: string[];
  files: string[];
}

function listChildren(files: string[], dir: string): Children {
  const prefix = dir === '' ? '' : dir + '/';
  const dirs = new Set<string>();
  const fileList: string[] = [];

  for (const f of files) {
    if (!f.startsWith(prefix)) continue;
    const rest = f.slice(prefix.length);
    if (rest === '') continue;
    const slash = rest.indexOf('/');
    if (slash === -1) fileList.push(f);
    else dirs.add(prefix + rest.slice(0, slash));
  }

  return { dirs: Array.from(dirs).sort(), files: fileList.sort() };
}

function parentDir(dir: string): string {
  const idx = dir.lastIndexOf('/');
  return idx === -1 ? '' : dir.slice(0, idx);
}

export function Breadcrumbs({ path, files, onOpenFile }: BreadcrumbsProps) {
  const segments = path.split('/');
  const fileName = segments[segments.length - 1];
  const folderSegments = segments.slice(0, -1);

  const [openDir, setOpenDir] = useState<string | null>(null);
  const rootRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    setOpenDir(null);
  }, [path]);

  useEffect(() => {
    if (openDir === null) return;
    function onClickOutside(e: MouseEvent) {
      if (rootRef.current && !rootRef.current.contains(e.target as HTMLElement)) {
        setOpenDir(null);
      }
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') setOpenDir(null);
    }
    document.addEventListener('mousedown', onClickOutside);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onClickOutside);
      document.removeEventListener('keydown', onKey);
    };
  }, [openDir]);

  function renderDropdown(dir: string) {
    const children = listChildren(files, dir);
    const isRoot = dir === '';
    return (
      <div className="breadcrumbs__menu">
        <div className="breadcrumbs__menu-header">
          <span className="breadcrumbs__menu-path">{isRoot ? 'Workspace' : dir}</span>
          {!isRoot && (
            <button className="breadcrumbs__menu-up" onClick={() => setOpenDir(parentDir(dir))} title="Up one level">
              ↑
            </button>
          )}
        </div>
        <div className="breadcrumbs__menu-list">
          {children.dirs.map((d) => (
            <button key={d} className="breadcrumbs__menu-item" onClick={() => setOpenDir(d)}>
              <FileTypeIcon path={d} isFolder size={13} />
              <span>{d.split('/').pop()}</span>
            </button>
          ))}
          {children.files.map((f) => (
            <button
              key={f}
              className="breadcrumbs__menu-item"
              onClick={() => {
                onOpenFile(f);
                setOpenDir(null);
              }}
            >
              <FileTypeIcon path={f} size={13} />
              <span>{f.split('/').pop()}</span>
            </button>
          ))}
          {children.dirs.length === 0 && children.files.length === 0 && (
            <div className="breadcrumbs__menu-empty">No files</div>
          )}
        </div>
      </div>
    );
  }

  return (
    <div className="breadcrumbs" ref={rootRef}>
      <button className="breadcrumbs__crumb" onClick={() => setOpenDir(openDir === '' ? null : '')}>
        <FileTypeIcon path="" isFolder size={13} />
        <span>Workspace</span>
      </button>

      {folderSegments.map((seg, i) => {
        const dir = folderSegments.slice(0, i + 1).join('/');
        return (
          <span key={dir} className="breadcrumbs__group">
            <span className="breadcrumbs__sep">/</span>
            <button
              className={`breadcrumbs__crumb ${openDir === dir ? 'breadcrumbs__crumb--active' : ''}`}
              onClick={() => setOpenDir(openDir === dir ? null : dir)}
            >
              <span>{seg}</span>
            </button>
          </span>
        );
      })}

      <span className="breadcrumbs__sep">/</span>
      <span className="breadcrumbs__leaf">
        <FileTypeIcon path={path} size={13} />
        <span>{fileName}</span>
      </span>

      {openDir !== null && renderDropdown(openDir)}
    </div>
  );
}
