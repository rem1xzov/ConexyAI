import { useEffect, useMemo, useRef } from 'react';
import Editor, { loader, type OnMount } from '@monaco-editor/react';
import { detectLanguageFromExtension } from '../utils/fileTypes';
import { useEffectiveTheme } from '../theme';

type EditorInstance = Parameters<OnMount>[0];
type MonacoApi = Parameters<OnMount>[1];

// MONACO_MODELS: добавлено 2026-09-24 (M12) — раньше у всех вкладок была ОДНА модель Monaco (не
// передавался `path`), поэтому стек undo был общим: лишний Ctrl+Z во вкладке B возвращал в неё текст
// вкладки A, и Ctrl+S записывал этот текст в файл B. Теперь у каждого файла каждого чата своя модель
// с собственным URI (а значит, свой undo и своё состояние прокрутки/курсора), а правки и Ctrl+S
// принимаются только от модели, которая действительно принадлежит открытому файлу.

const MODEL_ROOT = 'file:///workspace';

/** Monaco model URI of a workspace file: unique per chat, so files never share a model or undo. */
export function editorModelUri(chatId: string, path: string): string {
  const safePath = path
    .split('/')
    .filter(Boolean)
    .map((segment) => encodeURIComponent(segment))
    .join('/');
  return `${MODEL_ROOT}/${encodeURIComponent(chatId)}/${safePath}`;
}

/**
 * Disposes the Monaco models of one file (`path` given), of a folder (`path` ending with "/") or of
 * a whole chat (no `path`). Call it when a tab closes, a file is deleted or the chat's tabs are
 * dropped, so stale models (with stale text) are never reused and do not pile up in memory.
 */
export function disposeEditorModels(chatId: string, path?: string): void {
  const monaco = loader.__getMonacoInstance();
  if (!monaco) return;
  const prefix = monaco.Uri.parse(editorModelUri(chatId, '')).path.replace(/\/?$/, '/');
  const exact = path && !path.endsWith('/') ? monaco.Uri.parse(editorModelUri(chatId, path)).toString() : null;
  const folder = path && path.endsWith('/') ? monaco.Uri.parse(editorModelUri(chatId, path)).path.replace(/\/?$/, '/') : null;
  for (const model of monaco.editor.getModels()) {
    const uri = model.uri;
    if (uri.scheme !== 'file' || !uri.path.startsWith(prefix)) continue;
    if (exact && uri.toString() !== exact) continue;
    if (folder && !uri.path.startsWith(folder)) continue;
    if (!model.isDisposed()) model.dispose();
  }
}

interface CodeEditorProps {
  path: string;
  /** Model URI of this file (see editorModelUri). Falls back to the bare path. */
  modelPath?: string;
  content: string;
  onChange: (value: string) => void;
  onSave: () => void;
  /**
   * Bumped by the parent whenever it replaces `content` for a reason other than typing in this
   * editor (agent edit, "take the agent's version"). Only then is `content` written into the model.
   */
  revision?: number;
  /** Lines to flash after an agent edit, paired with a nonce so re-edits re-trigger. */
  highlight?: { lines: number[]; nonce: number } | null;
  onCursorChange?: (pos: { line: number; column: number; language: string }) => void;
}

/**
 * Monaco-backed code editor. Highlighting is derived from the file extension;
 * Ctrl/Cmd+S triggers an explicit save (no autosave to avoid racing the agent's
 * str_replace_editor edits).
 */
export function CodeEditor({ path, modelPath, content, revision = 0, onChange, onSave, highlight, onCursorChange }: CodeEditorProps) {
  const language = detectLanguageFromExtension(path);
  const effectiveTheme = useEffectiveTheme();
  const uri = modelPath ?? path;

  const editorRef = useRef<EditorInstance | null>(null);
  const monacoRef = useRef<MonacoApi | null>(null);
  const decorationIdsRef = useRef<string[]>([]);
  const highlightRef = useRef<{ lines: number[]; nonce: number } | null | undefined>(highlight);
  useEffect(() => {
    highlightRef.current = highlight;
  }, [highlight]);

  // Monaco's onMount runs once, so keep refs to the latest closures/values.
  const onSaveRef = useRef(onSave);
  const onChangeRef = useRef(onChange);
  const onCursorChangeRef = useRef(onCursorChange);
  const pathRef = useRef(path);
  const uriRef = useRef(uri);
  const contentRef = useRef(content);
  onSaveRef.current = onSave;
  onChangeRef.current = onChange;
  onCursorChangeRef.current = onCursorChange;
  pathRef.current = path;
  uriRef.current = uri;
  contentRef.current = content;

  /** True when the editor currently shows the model of the file this component is rendering. */
  function showsOwnModel(): boolean {
    const editor = editorRef.current;
    const monaco = monacoRef.current;
    const model = editor?.getModel();
    if (!editor || !monaco || !model) return false;
    return model.uri.toString() === monaco.Uri.parse(uriRef.current).toString();
  }

  /**
   * Writes `content` into the model as an undoable edit. Called only when the text is known to have
   * changed outside the editor: a new `revision`, a different file shown, or the first mount (a
   * model left over from an earlier mount may hold older text than the tab).
   *
   * The editor is deliberately uncontrolled (defaultValue, no `value` prop). The library's controlled
   * mode re-applies `value` from a passive effect; when keystrokes arrive faster than React commits,
   * that effect wrote an older value back into the model and ate the characters typed in between.
   */
  function syncModelWithContent() {
    const editor = editorRef.current;
    const model = editor?.getModel();
    if (!editor || !model || !showsOwnModel()) return;
    const next = contentRef.current;
    if (model.getValue() === next) return;
    editor.executeEdits('external', [{ range: model.getFullModelRange(), text: next, forceMoveMarkers: true }]);
    editor.pushUndoStop();
  }

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

  // Re-report the (restored) cursor position when the active file changes, and clear any
  // lingering edit highlight so it never bleeds onto the next file.
  useEffect(() => {
    const editor = editorRef.current;
    if (!editor) return;

    if (decorationIdsRef.current.length) {
      editor.deltaDecorations(decorationIdsRef.current, []);
      decorationIdsRef.current = [];
    }

    syncModelWithContent();

    const pos = editor.getPosition();
    if (pos) {
      onCursorChangeRef.current?.({
        line: pos.lineNumber,
        column: pos.column,
        language: detectLanguageFromExtension(pathRef.current),
      });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [uri]);

  // The parent replaced the text of the file on screen (not by typing here).
  useEffect(() => {
    syncModelWithContent();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [revision]);

  const options = useMemo(
    () => ({
      minimap: { enabled: true },
      fontSize: 14,
      automaticLayout: true,
      scrollBeyondLastLine: false,
      tabSize: 2,
      wordWrap: 'off' as const,
      renderWhitespace: 'selection' as const,
    }),
    [],
  );

  return (
    <Editor
      height="100%"
      path={uri}
      language={language}
      // Only seeds a model the first time a file is shown; later changes go through
      // syncModelWithContent (see above).
      defaultValue={content}
      // The parent owns model lifetimes (disposeEditorModels); unmounting the editor, e.g. to show
      // a binary file, must not throw away this file's undo history.
      keepCurrentModel
      theme={effectiveTheme === 'light' ? 'vs' : 'vs-dark'}
      onChange={(value) => {
        // Only edits of this file's own model count. During a tab switch Monaco may still report a
        // change of the previous model; attributing it to the new path is exactly how one file's
        // text used to end up in another.
        if (!showsOwnModel()) return;
        onChangeRef.current(value ?? '');
      }}
      onMount={(editor, monaco) => {
        editorRef.current = editor;
        monacoRef.current = monaco;

        syncModelWithContent();
        applyHighlight();

        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
          if (!showsOwnModel()) return;
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
      options={options}
    />
  );
}
