import { useSyncExternalStore } from 'react';
import type { ArtifactKind, ParsedArtifact } from '../../utils/artifacts';
import { hasPreview } from '../../utils/artifacts';

/**
 * ARTIFACTS: добавлено 2026-09-24 — крошечный внешний стор для панели артефактов.
 *
 * Панель монтируется из main.tsx (портал рядом с App), поэтому App.tsx о ней ничего не знает.
 * Связь идёт через этот модуль: пузыри сообщений регистрируют свои артефакты, карточки и кнопки
 * «Превью» открывают панель, панель читает состояние через useSyncExternalStore.
 *
 * Версии: регистрируются только артефакты смонтированных сообщений, то есть ленты текущего чата.
 * Одинаковый identifier в нескольких сообщениях = несколько версий одного артефакта, упорядоченных
 * по времени сообщения; по умолчанию показывается последняя.
 */

export interface ArtifactEntry extends ParsedArtifact {
  /** `${messageId}:${index}` — unique per artifact occurrence. */
  key: string;
  messageId: string;
  /** Sort key: message time, then position inside the message. */
  order: number;
}

export type ArtifactTab = 'preview' | 'code';

export interface OpenArtifact {
  /** Identifier of a registered artifact, or null for an ad-hoc preview (code block). */
  identifier: string | null;
  /** Specific version to show; null = the latest one. */
  key: string | null;
  /** Opened automatically while streaming: the tab may still follow the artifact's progress. */
  auto: boolean;
}

export interface AdhocArtifact {
  type: ArtifactKind;
  title: string;
  language?: string;
  content: string;
}

export interface ArtifactState {
  entries: ReadonlyMap<string, ArtifactEntry>;
  open: OpenArtifact | null;
  adhoc: AdhocArtifact | null;
  tab: ArtifactTab;
  /** Bumped on every explicit (user) open so the panel knows to move focus. */
  focusNonce: number;
}

let state: ArtifactState = {
  entries: new Map(),
  open: null,
  adhoc: null,
  tab: 'preview',
  focusNonce: 0,
};

const listeners = new Set<() => void>();

function setState(next: ArtifactState): void {
  state = next;
  listeners.forEach((listener) => listener());
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function getArtifactState(): ArtifactState {
  return state;
}

export function useArtifactState(): ArtifactState {
  return useSyncExternalStore(subscribe, getArtifactState, getArtifactState);
}

/** Subscribes to a derived primitive (re-renders only when it changes). */
export function useArtifactSelector<T extends string | number | boolean | null>(select: (s: ArtifactState) => T): T {
  return useSyncExternalStore(
    subscribe,
    () => select(state),
    () => select(state),
  );
}

/** All versions of an identifier, oldest first. */
export function versionsOf(s: ArtifactState, identifier: string): ArtifactEntry[] {
  const list: ArtifactEntry[] = [];
  s.entries.forEach((entry) => {
    if (entry.identifier === identifier) list.push(entry);
  });
  return list.sort((a, b) => a.order - b.order);
}

function sameEntry(a: ArtifactEntry | undefined, b: ArtifactEntry): boolean {
  return Boolean(
    a &&
      a.identifier === b.identifier &&
      a.type === b.type &&
      a.title === b.title &&
      a.language === b.language &&
      a.content === b.content &&
      a.complete === b.complete &&
      a.order === b.order,
  );
}

/**
 * Replaces the artifacts registered for one message. Cheap when nothing changed (no emit), so it
 * can run on every streamed token.
 */
export function registerMessageArtifacts(messageId: string, createdAt: number, artifacts: ParsedArtifact[]): void {
  const next = new Map(state.entries);
  let changed = false;

  const keep = new Set<string>();
  artifacts.forEach((artifact, index) => {
    const key = `${messageId}:${index}`;
    keep.add(key);
    const entry: ArtifactEntry = { ...artifact, key, messageId, order: createdAt * 1000 + index };
    if (!sameEntry(next.get(key), entry)) {
      next.set(key, entry);
      changed = true;
    }
  });
  next.forEach((entry, key) => {
    if (entry.messageId === messageId && !keep.has(key)) {
      next.delete(key);
      changed = true;
    }
  });
  if (!changed) return;

  let tab = state.tab;
  // An auto-opened artifact flips to its preview the moment it is complete (unless the user has
  // taken over the panel in the meantime).
  if (state.open?.auto && state.open.identifier && tab === 'code') {
    const versions = versionsOf({ ...state, entries: next }, state.open.identifier);
    const shown = versions[versions.length - 1];
    if (shown?.complete && hasPreview(shown.type)) tab = 'preview';
  }
  setState({ ...state, entries: next, tab });
}

export function unregisterMessage(messageId: string): void {
  let changed = false;
  const next = new Map(state.entries);
  next.forEach((entry, key) => {
    if (entry.messageId === messageId) {
      next.delete(key);
      changed = true;
    }
  });
  if (changed) setState({ ...state, entries: next });
}

/** Opens a registered artifact (a specific version when `key` is given). */
export function openArtifact(identifier: string, key: string | null = null, options: { auto?: boolean } = {}): void {
  const versions = versionsOf(state, identifier);
  const target = (key && state.entries.get(key)) || versions[versions.length - 1];
  const previewable = target ? hasPreview(target.type) : true;
  const auto = Boolean(options.auto);
  setState({
    ...state,
    open: { identifier, key, auto },
    adhoc: null,
    // Streaming artifacts start on the code (that is what is arriving); finished ones on preview.
    tab: previewable && (!target || target.complete) ? 'preview' : 'code',
    focusNonce: auto ? state.focusNonce : state.focusNonce + 1,
  });
}

/** Opens a one-off preview that is not tied to an artifact tag (e.g. a ```html code block). */
export function openAdhocArtifact(artifact: AdhocArtifact): void {
  setState({
    ...state,
    open: { identifier: null, key: null, auto: false },
    adhoc: artifact,
    tab: hasPreview(artifact.type) ? 'preview' : 'code',
    focusNonce: state.focusNonce + 1,
  });
}

export function selectArtifactVersion(key: string): void {
  if (!state.open) return;
  setState({ ...state, open: { ...state.open, key, auto: false } });
}

export function setArtifactTab(tab: ArtifactTab): void {
  if (!state.open) return;
  setState({ ...state, tab, open: { ...state.open, auto: false } });
}

export function closeArtifact(): void {
  if (!state.open) return;
  setState({ ...state, open: null, adhoc: null });
}

/** Resolves what the panel should show right now, or null. */
export function resolveOpenArtifact(s: ArtifactState): {
  artifact: AdhocArtifact & { complete: boolean; identifier: string | null };
  versions: ArtifactEntry[];
  current: ArtifactEntry | null;
} | null {
  if (!s.open) return null;
  if (s.open.identifier === null) {
    return s.adhoc ? { artifact: { ...s.adhoc, complete: true, identifier: null }, versions: [], current: null } : null;
  }
  const versions = versionsOf(s, s.open.identifier);
  if (versions.length === 0) return null;
  const pinned = s.open.key ? versions.find((v) => v.key === s.open!.key) : undefined;
  const current = pinned ?? versions[versions.length - 1];
  return { artifact: current, versions, current };
}
