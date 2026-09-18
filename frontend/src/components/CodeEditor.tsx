import { useEffect, useRef } from 'react';
import Editor, { type OnMount } from '@monaco-editor/react';
import { detectLanguageFromExtension } from '../utils/fileTypes';

type EditorInstance = Parameters<OnMount>[0];
type MonacoApi = Parameters<OnMount>[1];

interface CodeEditorProps {
  path: string;
  content: string;
  onChange: (value: string) => void;
  onSave: () => void;
  /** Lines to flash after an agent edit, paired with a nonce so re-edits re-trigger. */
  highlight?: { lines: number[]; nonce: number } | null;
  onCursorChange?: (pos: { line: number; column: number; language: string }) => void;
}

/**
 * Monaco-backed code editor. Highlighting is derived from the file extension;
 * Ctrl/Cmd+S triggers an explicit save (no autosave to avoid racing the agent's
 * str_replace_editor edits).
 */
export function CodeEditor({ path, content, onChange, onSave, highlight, onCursorChange }: CodeEditorProps) {
  const language = detectLanguageFromExtension(path);

  const editorRef = useRef<EditorInstance | null>(null);
  const monacoRef = useRef<MonacoApi | null>(null);
  const decorationIdsRef = useRef<string[]>([]);
  const highlightRef = useRef<{ lines: number[]; nonce: number } | null | undefined>(highlight);
  useEffect(() => {
    highlightRef.current = highlight;
  }, [highlight]);

  // Flash and reveal the highlighted lines. Kept in a function (referenced from both the
  // effect and onMount) so a highlight set right before the editor mounts is still applied.
  const applyHighlight = () => {
    const editor = editorRef.current;
    const monaco = monacoRef.current;
    const h = highlightRef.current;
    if (!editor || !monaco || !h || h.lines.length === 0) return;

    if (decorationIdsRef.current.length) {
      editor.deltaDecorations(decorationIdsRef.current, []);
      decorationIdsRef.current = [];
    }

    editor.revealLineInCenter(h.lines[0]);

    const ids = editor.deltaDecorations(
      [],
      h.lines.map((line) => ({
        range: new monaco.Range(line, 1, line, 1),
        options: { isWholeLine: true, className: 'agent-edit-highlight' },
      })),
    );
    decorationIdsRef.current = ids;

    const timer = setTimeout(() => {
      if (decorationIdsRef.current === ids) {
        editor.deltaDecorations(ids, []);
        decorationIdsRef.current = [];
      }
    }, 2500);

    return () => clearTimeout(timer);
  };

  // Flash the lines an agent edit (or a clicked Problem) touched, then fade them away.
  useEffect(() => {
    return applyHighlight();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [highlight?.nonce, highlight?.lines]);

  // Monaco's onMount runs once, so keep refs to the latest closures/values.
  const onSaveRef = useRef(onSave);
  useEffect(() => {
    onSaveRef.current = onSave;
  }, [onSave]);

  const onCursorChangeRef = useRef(onCursorChange);
  useEffect(() => {
    onCursorChangeRef.current = onCursorChange;
  }, [onCursorChange]);

  const pathRef = useRef(path);
  useEffect(() => {
    pathRef.current = path;
  }, [path]);

  // Re-report the (reset) cursor position when the active file changes, and clear any
  // lingering edit highlight so it never bleeds onto the next file.
  useEffect(() => {
    const editor = editorRef.current;
    if (!editor) return;

    if (decorationIdsRef.current.length) {
      editor.deltaDecorations(decorationIdsRef.current, []);
      decorationIdsRef.current = [];
    }

    const pos = editor.getPosition();
    if (pos) {
      onCursorChangeRef.current?.({
        line: pos.lineNumber,
        column: pos.column,
        language: detectLanguageFromExtension(pathRef.current),
      });
    }
  }, [path]);

  return (
    <Editor
      height="100%"
      language={language}
      value={content}
      theme="vs-dark"
      onChange={(value) => onChange(value ?? '')}
      onMount={(editor, monaco) => {
        editorRef.current = editor;
        monacoRef.current = monaco;

        applyHighlight();

        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
          onSaveRef.current();
        });

        editor.onDidChangeCursorPosition((e: { position: { lineNumber: number; column: number } }) => {
          onCursorChangeRef.current?.({
            line: e.position.lineNumber,
            column: e.position.column,
            language: detectLanguageFromExtension(pathRef.current),
          });
        });

        const pos = editor.getPosition();
        if (pos) {
          onCursorChangeRef.current?.({
            line: pos.lineNumber,
            column: pos.column,
            language: detectLanguageFromExtension(pathRef.current),
          });
        }
      }}
      options={{
        minimap: { enabled: true },
        fontSize: 14,
        automaticLayout: true,
        scrollBeyondLastLine: false,
        tabSize: 2,
        wordWrap: 'off',
        renderWhitespace: 'selection',
      }}
    />
  );
}
