import { DiffEditor } from '@monaco-editor/react';
import { detectLanguageFromExtension } from '../utils/fileTypes';
import { useEffectiveTheme } from '../theme';

// IDE_DIFF: добавлено 2026-10-05 — режим сравнения «до/после» на Monaco DiffEditor: слева ревизия
// (обычно HEAD), справа рабочая копия. Переключатель side-by-side / inline задаётся снаружи.
interface DiffViewProps {
  path: string;
  original: string;
  modified: string;
  sideBySide: boolean;
}

export function DiffView({ path, original, modified, sideBySide }: DiffViewProps) {
  const language = detectLanguageFromExtension(path);
  const effectiveTheme = useEffectiveTheme();

  return (
    <DiffEditor
      height="100%"
      original={original}
      modified={modified}
      language={language}
      theme={effectiveTheme === 'light' ? 'vs' : 'vs-dark'}
      options={{
        readOnly: true,
        renderSideBySide: sideBySide,
        automaticLayout: true,
        fontSize: 13,
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        renderOverviewRuler: false,
        originalEditable: false,
        // The two originals/modified panes should scroll together for a change-by-change read.
        scrollBeyondLastColumn: 2,
      }}
    />
  );
}
