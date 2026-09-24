import { useCallback, useEffect, useRef, useState } from 'react';
import type { DragEvent as ReactDragEvent } from 'react';

// FILE_DROP: добавлено 2026-09-24 (L11)
// Файл, брошенный в окно мимо зоны приёма, браузер по умолчанию ОТКРЫВАЕТ вместо страницы — вместе
// с этим пропадали черновик и идущий стрим. Теперь окно гасит такое действие, а колонка чата
// принимает файлы как вложения.

function hasFiles(e: DragEvent | ReactDragEvent): boolean {
  const types = e.dataTransfer?.types;
  if (!types) return false;
  return Array.from(types).includes('Files');
}

/**
 * Stops the browser from navigating to a file dropped outside a drop zone. Drop zones call
 * `preventDefault` themselves first (React handlers run before this window listener), so they keep
 * working; anything else just gets a "not allowed" cursor.
 */
export function usePreventWindowFileDrop(): void {
  useEffect(() => {
    const onDragOver = (e: DragEvent) => {
      if (!hasFiles(e) || e.defaultPrevented) return;
      e.preventDefault();
      if (e.dataTransfer) e.dataTransfer.dropEffect = 'none';
    };
    const onDrop = (e: DragEvent) => {
      if (!hasFiles(e) || e.defaultPrevented) return;
      e.preventDefault();
    };
    window.addEventListener('dragover', onDragOver);
    window.addEventListener('drop', onDrop);
    return () => {
      window.removeEventListener('dragover', onDragOver);
      window.removeEventListener('drop', onDrop);
    };
  }, []);
}

export interface DropZone {
  /** A file drag is currently over the zone (show the overlay). */
  active: boolean;
  handlers: {
    onDragEnter: (e: ReactDragEvent) => void;
    onDragOver: (e: ReactDragEvent) => void;
    onDragLeave: (e: ReactDragEvent) => void;
    onDrop: (e: ReactDragEvent) => void;
  };
}

/** Turns an element into a drop target for files. Disabled zones ignore drags entirely. */
export function useFileDropZone(onFiles: (files: File[]) => void, enabled: boolean): DropZone {
  const [active, setActive] = useState(false);
  // dragenter/dragleave fire for every child element; a counter tells the real leave apart.
  const depthRef = useRef(0);
  const onFilesRef = useRef(onFiles);
  onFilesRef.current = onFiles;

  useEffect(() => {
    if (!enabled) {
      depthRef.current = 0;
      setActive(false);
    }
  }, [enabled]);

  const onDragEnter = useCallback((e: ReactDragEvent) => {
    if (!enabled || !hasFiles(e)) return;
    e.preventDefault();
    depthRef.current += 1;
    setActive(true);
  }, [enabled]);

  const onDragOver = useCallback((e: ReactDragEvent) => {
    if (!enabled || !hasFiles(e)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
  }, [enabled]);

  const onDragLeave = useCallback((e: ReactDragEvent) => {
    if (!enabled || !hasFiles(e)) return;
    depthRef.current = Math.max(0, depthRef.current - 1);
    if (depthRef.current === 0) setActive(false);
  }, [enabled]);

  const onDrop = useCallback((e: ReactDragEvent) => {
    if (!enabled || !hasFiles(e)) return;
    e.preventDefault();
    depthRef.current = 0;
    setActive(false);
    const files = Array.from(e.dataTransfer.files ?? []);
    if (files.length > 0) onFilesRef.current(files);
  }, [enabled]);

  return { active, handlers: { onDragEnter, onDragOver, onDragLeave, onDrop } };
}
