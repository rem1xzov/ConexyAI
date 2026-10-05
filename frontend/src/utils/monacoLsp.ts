import type { OnMount } from '@monaco-editor/react';
import { findIdeDefinition, getIdeHover, getIdeSymbols, searchIdeSymbols } from '../api/conexyApi';
import type { IdeSymbolNode } from '../types/api';

// LSP_LITE: добавлено 2026-10-05 — регистрирует в Monaco провайдеры go-to-definition, hover,
// document symbols и completion поверх облегчённого бэкенда (разбор символов + grep по воркспейсу).
// Это не настоящий языковой сервер: типы не вычисляются, поэтому автодополнение идёт по символам
// проекта, а не полноценный IntelliSense.

export type MonacoApi = Parameters<OnMount>[1];
export type MonacoEditor = Parameters<OnMount>[0];

const LANGUAGES = ['csharp', 'typescript', 'javascript', 'python', 'go', 'rust', 'php', 'ruby', 'java'];
const MODEL_PREFIX = 'file:///workspace/';

/** Dispatched when go-to-definition lands in another file; WorkspacePanel opens it. */
export const OPEN_LOCATION_EVENT = 'conexy:open-location';

const registeredInstances = new WeakSet<object>();

/** Parses `file:///workspace/<chatId>/<path>` back into the chat id and the workspace-relative path. */
export function parseModelUri(uri: { toString(): string }): { sessionId: string; path: string } | null {
  const raw = uri.toString();
  if (!raw.startsWith(MODEL_PREFIX)) return null;
  const rest = raw.slice(MODEL_PREFIX.length);
  const slash = rest.indexOf('/');
  if (slash <= 0) return null;
  try {
    const sessionId = decodeURIComponent(rest.slice(0, slash));
    const path = rest
      .slice(slash + 1)
      .split('/')
      .filter(Boolean)
      .map((segment) => decodeURIComponent(segment))
      .join('/');
    if (!path) return null;
    return { sessionId, path };
  } catch {
    return null;
  }
}

function openLocation(path: string, line: number, column: number): void {
  window.dispatchEvent(new CustomEvent(OPEN_LOCATION_EVENT, { detail: { path, line, column } }));
}

export function registerLspProviders(monaco: MonacoApi): void {
  if (registeredInstances.has(monaco)) return;
  registeredInstances.add(monaco);

  const definition = {
    provideDefinition: async (model: MonadoModel, position: MonadoPosition, token: MonadoToken) => {
      const info = parseModelUri(model.uri);
      if (!info) return null;
      const word = model.getWordAtPosition(position);
      if (!word) return null;

      try {
        const result = await findIdeDefinition(info.sessionId, info.path, word.word, model.getValue());
        if (token.isCancellationRequested || !result.found) return null;

        const sameFile = result.locations.filter((l) => l.path === info.path);
        if (sameFile.length > 0) {
          return sameFile.map((l) => ({
            uri: model.uri,
            range: new monaco.Range(l.line, l.column, l.line, l.column),
          }));
        }

        // Cross-file: Monaco is bound to React-managed models, so ask the panel to open the file.
        const other = result.locations[0];
        openLocation(other.path, other.line, other.column);
        return null;
      } catch {
        return null;
      }
    },
  };

  const hover = {
    provideHover: async (model: MonadoModel, position: MonadoPosition, token: MonadoToken) => {
      const info = parseModelUri(model.uri);
      if (!info) return null;
      const word = model.getWordAtPosition(position);
      if (!word) return null;

      try {
        const result = await getIdeHover(info.sessionId, info.path, word.word, model.getValue());
        if (token.isCancellationRequested || !result.found) return null;

        const parts: string[] = [];
        if (result.signature) parts.push('```\n' + result.signature + '\n```');
        if (result.documentation) parts.push(result.documentation);
        if (parts.length === 0) return null;

        return {
          range: new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn),
          contents: [{ value: parts.join('\n\n') }],
        };
      } catch {
        return null;
      }
    },
  };

  const documentSymbols = {
    provideDocumentSymbols: async (model: MonadoModel, token: MonadoToken) => {
      const info = parseModelUri(model.uri);
      if (!info) return [];
      const symbols = await symbolsFor(model, info.sessionId, info.path);
      if (token.isCancellationRequested) return [];
      return symbols.map((s) => toDocumentSymbol(monaco, s));
    },
  };

  const completion = {
    triggerCharacters: ['.', ':', '<', '@', '/'],
    provideCompletionItems: async (model: MonadoModel, position: MonadoPosition, _context: unknown, token: MonadoToken) => {
      const info = parseModelUri(model.uri);
      if (!info) return { suggestions: [] };

      const word = model.getWordUntilPosition(position);
      const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
      const seen = new Set<string>();
      const suggestions: unknown[] = [];

      const add = (label: string, kind: string, detail: string | null | undefined, insertText?: string) => {
        if (!label || seen.has(label)) return;
        seen.add(label);
        suggestions.push({
          label,
          kind: completionKind(monaco, kind),
          detail: detail ?? undefined,
          insertText: insertText ?? label,
          range,
        });
      };

      try {
        const fileSymbols = await symbolsFor(model, info.sessionId, info.path);
        for (const symbol of flatten(fileSymbols)) add(symbol.name, symbol.kind, symbol.detail);

        if (word.word.length >= 2) {
          const remote = await searchSymbolsCached(info.sessionId, word.word);
          if (token.isCancellationRequested) return { suggestions: [] };
          for (const symbol of remote) add(symbol.name, symbol.kind, `${symbol.path}:${symbol.line}`);
        }
      } catch {
        /* completion must never throw into the editor */
      }

      return { suggestions };
    },
  };

  for (const language of LANGUAGES) {
    monaco.languages.registerDefinitionProvider(language, definition as never);
    monaco.languages.registerHoverProvider(language, hover as never);
    monaco.languages.registerDocumentSymbolProvider(language, documentSymbols as never);
    monaco.languages.registerCompletionItemProvider(language, completion as never);
  }
}

// Structural model/position types, so the module does not need the full monaco-editor types.
interface MonadoModel {
  uri: { toString(): string };
  getValue(): string;
  getWordAtPosition(position: MonadoPosition): { word: string; startColumn: number; endColumn: number } | null;
  getWordUntilPosition(position: MonadoPosition): { word: string; startColumn: number; endColumn: number };
}
interface MonadoPosition {
  lineNumber: number;
  column: number;
}
interface MonadoToken {
  isCancellationRequested: boolean;
}

// --- caching ---------------------------------------------------------------------------------

const SYMBOL_TTL = 3000;
const SEARCH_TTL = 8000;

interface CacheEntry<T> {
  at: number;
  value: T;
}

const symbolCache = new Map<string, CacheEntry<IdeSymbolNode[]>>();
const searchCache = new Map<string, CacheEntry<{ name: string; kind: string; path: string; line: number }[]>>();

async function symbolsFor(model: MonadoModel, sessionId: string, path: string): Promise<IdeSymbolNode[]> {
  const key = `${sessionId}:${path}`;
  const cached = symbolCache.get(key);
  if (cached && Date.now() - cached.at < SYMBOL_TTL) return cached.value;

  const result = await getIdeSymbols(sessionId, path, model.getValue());
  const symbols = result.success ? result.symbols ?? [] : [];
  if (symbolCache.size > 300) symbolCache.clear();
  symbolCache.set(key, { at: Date.now(), value: symbols });
  return symbols;
}

async function searchSymbolsCached(sessionId: string, query: string) {
  const key = `${sessionId}:${query.toLowerCase()}`;
  const cached = searchCache.get(key);
  if (cached && Date.now() - cached.at < SEARCH_TTL) return cached.value;

  const result = await searchIdeSymbols(sessionId, query, 40);
  const value = (result.success ? result.symbols ?? [] : []).map((s) => ({
    name: s.name,
    kind: s.kind,
    path: s.path,
    line: s.line,
  }));

  if (searchCache.size > 200) searchCache.clear();
  searchCache.set(key, { at: Date.now(), value });
  return value;
}

// --- mapping helpers -------------------------------------------------------------------------

export function flatten(nodes: IdeSymbolNode[]): IdeSymbolNode[] {
  const out: IdeSymbolNode[] = [];
  const walk = (list: IdeSymbolNode[]) => {
    for (const node of list) {
      out.push(node);
      walk(node.children ?? []);
    }
  };
  walk(nodes);
  return out;
}

function completionKind(monaco: MonacoApi, kind: string): number {
  const K = monaco.languages.CompletionItemKind;
  switch (kind) {
    case 'class':
      return K.Class;
    case 'interface':
      return K.Interface;
    case 'struct':
      return K.Struct;
    case 'enum':
      return K.Enum;
    case 'module':
      return K.Module;
    case 'type':
      return K.TypeParameter;
    case 'method':
      return K.Method;
    case 'function':
      return K.Function;
    case 'property':
      return K.Property;
    case 'field':
      return K.Field;
    case 'variable':
      return K.Variable;
    default:
      return K.Text;
  }
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function toDocumentSymbol(monaco: MonacoApi, node: IdeSymbolNode): any {
  return {
    name: node.name,
    detail: node.detail ?? '',
    kind: documentSymbolKind(monaco, node.kind),
    tags: [],
    range: new monaco.Range(node.line, node.column, Math.max(node.endLine, node.line), Math.max(node.endColumn, 1)),
    selectionRange: new monaco.Range(node.line, node.column, node.line, node.column + node.name.length),
    children: (node.children ?? []).map((child) => toDocumentSymbol(monaco, child)),
  };
}

function documentSymbolKind(monaco: MonacoApi, kind: string): number {
  const K = monaco.languages.SymbolKind;
  switch (kind) {
    case 'class':
      return K.Class;
    case 'interface':
      return K.Interface;
    case 'struct':
      return K.Struct;
    case 'enum':
      return K.Enum;
    case 'module':
      return K.Module;
    case 'method':
      return K.Method;
    case 'function':
      return K.Function;
    case 'property':
      return K.Property;
    case 'field':
      return K.Field;
    case 'variable':
      return K.Variable;
    default:
      return K.Variable;
  }
}
