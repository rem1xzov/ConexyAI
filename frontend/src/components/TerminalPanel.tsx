import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Terminal, type ITheme } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import { signalrService } from '../services/signalrService';
import {
  cancelSandboxCommand,
  getTerminalMode,
  hubErrorText,
  isForbiddenError,
  runSandboxCommand,
} from '../services/terminalHub';
import { useEffectiveTheme } from '../theme';

interface TerminalPanelProps {
  sessionId: string;
  /** Focus the terminal as soon as it is ready (e.g. right after the user opened it). */
  autoFocus?: boolean;
}

type Translate = (key: string, options?: Record<string, unknown>) => string;

type Phase =
  | { kind: 'loading' }
  | { kind: 'ready'; mode: 'pty' | 'sandbox' }
  | { kind: 'unavailable' }
  | { kind: 'error'; message: string };

interface TerminalSession {
  /** Called after xterm changed its size (fit), so a pty can be resized too. */
  onResize: (cols: number, rows: number) => void;
  dispose: () => void;
}

const DARK_THEME: ITheme = {
  background: '#12141a',
  foreground: '#ece7ee',
  cursor: '#c68bd4',
  cursorAccent: '#12141a',
  selectionBackground: 'rgba(198, 139, 212, 0.32)',
};

// ANSI colours tuned for a light background: xterm's defaults (bright yellow/white) are unreadable
// on it.
const LIGHT_THEME: ITheme = {
  background: '#fff7fa',
  foreground: '#1c1917',
  cursor: '#9b4fb8',
  cursorAccent: '#fff7fa',
  selectionBackground: 'rgba(155, 79, 184, 0.24)',
  black: '#1c1917',
  red: '#c62828',
  green: '#2e7d32',
  yellow: '#8d6e00',
  blue: '#1565c0',
  magenta: '#8e24aa',
  cyan: '#00838f',
  white: '#57534e',
  brightBlack: '#78716c',
  brightRed: '#d32f2f',
  brightGreen: '#388e3c',
  brightYellow: '#9e7c00',
  brightBlue: '#1976d2',
  brightMagenta: '#9c27b0',
  brightCyan: '#0097a7',
  brightWhite: '#44403c',
};

const red = (s: string) => `\x1b[31m${s}\x1b[0m`;
const yellow = (s: string) => `\x1b[33m${s}\x1b[0m`;
const dim = (s: string) => `\x1b[2m${s}\x1b[0m`;

// ---------------------------------------------------------------------------------------------
// PTY mode (dev only): the server runs a real shell, xterm is a dumb pipe in both directions.
// ---------------------------------------------------------------------------------------------

function startPtySession(term: Terminal, chatId: string, t: Translate): TerminalSession {
  let disposed = false;
  let started = false;

  const unsubscribe = signalrService.onTerminalOutput(chatId, (data) => {
    if (!disposed) term.write(data);
  });

  const input = term.onData((data) => {
    if (!started) return;
    void signalrService.sendTerminalInput(chatId, data).catch(() => {
      // The connection is being restored; keystrokes typed meanwhile are dropped, like a laggy ssh.
    });
  });

  void (async () => {
    try {
      // Join the chat group (task_{chatId}) so this connection receives both the pty output and
      // the agent's mirrored bash output.
      await signalrService.joinTask(chatId);
      await signalrService.startTerminal(chatId);
      if (disposed) return;
      started = true;
      await signalrService.resizeTerminal(chatId, term.cols, term.rows);
    } catch (e) {
      if (disposed) return;
      const message = isForbiddenError(e) ? t('terminal.forbidden') : t('terminal.startFailed', { message: hubErrorText(e) });
      term.write(`${red(message)}\r\n`);
    }
  })();

  return {
    onResize: (cols, rows) => {
      if (started) void signalrService.resizeTerminal(chatId, cols, rows).catch(() => {});
    },
    dispose: () => {
      disposed = true;
      unsubscribe();
      input.dispose();
      // The pty is keyed by chat id and deliberately survives the panel (no StopTerminal here).
    },
  };
}

// ---------------------------------------------------------------------------------------------
// Sandbox mode: line editing happens here, in the browser; a finished line is sent to the server
// as one RunSandboxCommand. Output (the user's own commands and the agent's mirrored bash calls)
// arrives as TerminalOutput events of the chat group.
// ---------------------------------------------------------------------------------------------

const PROMPT_TEXT = 'sandbox:/workspace$ ';
const PROMPT = '\x1b[1;32msandbox\x1b[0m:\x1b[1;34m/workspace\x1b[0m$ ';
const HISTORY_LIMIT = 200;

// Per-chat command history, kept for the lifetime of the page (switching chats and back keeps it).
// Deliberately not persisted: typed commands can contain secrets.
const historyByChat = new Map<string, string[]>();

function historyFor(chatId: string): string[] {
  let list = historyByChat.get(chatId);
  if (!list) {
    list = [];
    historyByChat.set(chatId, list);
  }
  return list;
}

/** Drops control characters from typed/pasted text; tabs become a space. */
function cleanText(text: string): string {
  return text.replace(/\t/g, ' ').replace(/[\x00-\x1f\x7f]/g, '');
}

function startSandboxSession(term: Terminal, chatId: string, t: Translate): TerminalSession {
  const history = historyFor(chatId);
  let historyIndex = history.length; // === history.length → editing a fresh line
  let draftBeforeHistory = '';

  let input = '';
  let cursor = 0; // index into `input`
  // Where the terminal cursor is now, as an offset from the start of the prompt line, and whether
  // the prompt + input are on screen at all. Kept exact so any edit can erase and redraw them.
  let renderedOffset = 0;
  let promptShown = false;

  let busy = false;
  let joined = false;
  let forbidden = false;
  let disposed = false;
  let atLineStart = true; // did the last output end with a line break?
  const queue: { text: string; run: boolean }[] = []; // further lines of a multi-line paste

  const cols = () => Math.max(1, term.cols);
  const rowOf = (offset: number) => Math.floor(offset / cols());

  /** Escape sequence that moves back to the prompt start and clears the prompt and input. */
  function eraseSeq(): string {
    if (!promptShown) return '';
    const up = rowOf(renderedOffset);
    promptShown = false;
    renderedOffset = 0;
    return `${up > 0 ? `\x1b[${up}A` : ''}\r\x1b[J`;
  }

  /** Escape sequence that prints the prompt and input and puts the cursor at `cursor`. */
  function drawSeq(): string {
    const total = PROMPT_TEXT.length + input.length;
    let seq = PROMPT + input;
    // A line ending exactly at the right edge leaves xterm in "pending wrap" (the cursor stays on
    // the last column). Force the wrap so the position math below stays exact.
    if (total > 0 && total % cols() === 0) seq += ' \b';
    const target = PROMPT_TEXT.length + cursor;
    const up = rowOf(total) - rowOf(target);
    if (up > 0) seq += `\x1b[${up}A`;
    const col = target % cols();
    seq += `\r${col > 0 ? `\x1b[${col}C` : ''}`;
    promptShown = true;
    renderedOffset = target;
    return seq;
  }

  function redraw() {
    if (disposed) return;
    term.write(eraseSeq() + drawSeq());
  }

  function showPrompt() {
    if (disposed || forbidden) return;
    term.write((atLineStart ? '' : '\r\n') + drawSeq());
    atLineStart = true;
  }

  /** Prints a notice on its own line(s), keeping the prompt (if shown) below it. */
  function printLine(text: string) {
    if (disposed) return;
    const hadPrompt = promptShown;
    term.write(eraseSeq() + (atLineStart ? '' : '\r\n') + text + '\r\n');
    atLineStart = true;
    if (hadPrompt && !forbidden) term.write(drawSeq());
  }

  function writeOutput(data: string) {
    if (disposed || !data) return;
    if (busy || !promptShown) {
      term.write(data);
      atLineStart = /[\r\n]$/.test(data);
      return;
    }
    // Output while the user is at the prompt (the agent's commands): lift the prompt out of the
    // way, print, and put the prompt with the half-typed line back underneath.
    let seq = eraseSeq() + data;
    if (!/[\r\n]$/.test(data)) seq += '\r\n';
    atLineStart = true;
    term.write(seq + drawSeq());
  }

  async function ensureJoined(): Promise<void> {
    if (joined) return;
    await signalrService.joinTask(chatId);
    joined = true;
  }

  function pushHistory(command: string) {
    if (history[history.length - 1] !== command) history.push(command);
    if (history.length > HISTORY_LIMIT) history.splice(0, history.length - HISTORY_LIMIT);
    historyIndex = history.length;
    draftBeforeHistory = '';
  }

  function takeQueued() {
    const next = queue.shift();
    if (!next) {
      showPrompt();
      return;
    }
    input = next.text;
    cursor = input.length;
    showPrompt();
    if (next.run) void submit();
  }

  async function submit() {
    if (disposed || busy || forbidden) return;
    const line = input;
    // Leave the finished line on screen with the cursor after it, then start a new line.
    cursor = input.length;
    // drawSeq() already moved to a fresh row when the line ends exactly at the right edge.
    const endsAtEdge = (PROMPT_TEXT.length + input.length) % cols() === 0;
    term.write(eraseSeq() + drawSeq() + (endsAtEdge ? '' : '\r\n'));
    promptShown = false;
    renderedOffset = 0;
    atLineStart = true;
    input = '';
    cursor = 0;

    const command = line.trim();
    if (!command) {
      takeQueued();
      return;
    }
    pushHistory(command);

    busy = true;
    try {
      await ensureJoined();
      const res = await runSandboxCommand(chatId, command);
      if (disposed) return;
      if (!atLineStart) term.write('\r\n');
      atLineStart = true;
      if (res.error === 'busy') {
        term.write(`${yellow(t('terminal.busy'))}\r\n`);
      } else if (res.error) {
        term.write(`${red(t('terminal.commandError', { message: res.error }))}\r\n`);
      } else if (res.timedOut) {
        term.write(`${yellow(t('terminal.timedOut'))}\r\n`);
      } else if (res.exitCode !== 0) {
        term.write(`${red(t('terminal.exitCode', { code: res.exitCode }))}\r\n`);
      }
    } catch (e) {
      if (disposed) return;
      if (!atLineStart) term.write('\r\n');
      atLineStart = true;
      if (isForbiddenError(e)) {
        forbidden = true;
        queue.length = 0;
        term.write(`${red(t('terminal.forbidden'))}\r\n`);
      } else {
        term.write(`${red(t('terminal.runFailed', { message: hubErrorText(e) }))}\r\n`);
      }
    } finally {
      busy = false;
    }
    if (!disposed && !forbidden) takeQueued();
  }

  function insert(text: string) {
    if (!text) return;
    input = input.slice(0, cursor) + text + input.slice(cursor);
    cursor += text.length;
  }

  function interrupt() {
    if (busy) {
      term.write(dim('^C'));
      atLineStart = false;
      queue.length = 0;
      void cancelSandboxCommand(chatId).catch(() => {
        // Nothing to cancel any more or the connection is down: the command result will tell.
      });
      return;
    }
    // Like a shell: abandon the current line and start a fresh prompt.
    cursor = input.length;
    term.write(eraseSeq() + drawSeq() + dim('^C') + '\r\n');
    promptShown = false;
    renderedOffset = 0;
    atLineStart = true;
    input = '';
    cursor = 0;
    queue.length = 0;
    historyIndex = history.length;
    showPrompt();
  }

  function handleData(data: string) {
    if (disposed || forbidden) return;
    if (data === '\x03') {
      interrupt();
      return;
    }
    // One command at a time: keystrokes while a command runs are ignored (Ctrl+C still works).
    if (busy) return;

    switch (data) {
      case '\x1b[A':
      case '\x1bOA':
        if (history.length === 0 || historyIndex === 0) return;
        if (historyIndex === history.length) draftBeforeHistory = input;
        historyIndex -= 1;
        input = history[historyIndex];
        cursor = input.length;
        redraw();
        return;
      case '\x1b[B':
      case '\x1bOB':
        if (historyIndex >= history.length) return;
        historyIndex += 1;
        input = historyIndex === history.length ? draftBeforeHistory : history[historyIndex];
        cursor = input.length;
        redraw();
        return;
      case '\x1b[C':
      case '\x1bOC':
        if (cursor < input.length) {
          cursor += 1;
          redraw();
        }
        return;
      case '\x1b[D':
      case '\x1bOD':
        if (cursor > 0) {
          cursor -= 1;
          redraw();
        }
        return;
      case '\x1b[H':
      case '\x1bOH':
      case '\x1b[1~':
      case '\x01': // Ctrl+A
        cursor = 0;
        redraw();
        return;
      case '\x1b[F':
      case '\x1bOF':
      case '\x1b[4~':
      case '\x05': // Ctrl+E
        cursor = input.length;
        redraw();
        return;
      case '\x1b[3~': // Delete
        if (cursor < input.length) {
          input = input.slice(0, cursor) + input.slice(cursor + 1);
          redraw();
        }
        return;
      case '\x7f':
      case '\b': // Backspace
        if (cursor > 0) {
          input = input.slice(0, cursor - 1) + input.slice(cursor);
          cursor -= 1;
          redraw();
        }
        return;
      case '\x15': // Ctrl+U — delete up to the cursor
        input = input.slice(cursor);
        cursor = 0;
        redraw();
        return;
      case '\x17': {
        // Ctrl+W — delete the word before the cursor
        const before = input.slice(0, cursor).replace(/\S+\s*$/, '');
        input = before + input.slice(cursor);
        cursor = before.length;
        redraw();
        return;
      }
      case '\x0c': // Ctrl+L — clear the screen, keep the line being typed
        term.write(eraseSeq());
        term.clear();
        term.write(drawSeq());
        return;
      default:
        break;
    }

    // Any other escape sequence (function keys, Alt+…) has no meaning in line mode.
    if (data.startsWith('\x1b')) return;

    // Typed text or a paste. Line breaks act as Enter; the lines after the first one are queued
    // and run one after another, like pasting into a shell.
    const parts = data.split(/\r\n|\r|\n/);
    insert(cleanText(parts[0]));
    if (parts.length === 1) {
      redraw();
      return;
    }
    for (let i = 1; i < parts.length; i += 1) {
      queue.push({ text: cleanText(parts[i]), run: i < parts.length - 1 });
    }
    void submit();
  }

  const unsubscribe = signalrService.onTerminalOutput(chatId, writeOutput);
  const inputDisposable = term.onData(handleData);

  term.write(`${dim(t('terminal.sandboxBanner'))}\r\n`);
  atLineStart = true;
  showPrompt();

  // Join the chat group right away so the agent's commands show up too, not only after the first
  // command of our own. A refusal means the chat is not this user's (contract C-6).
  void ensureJoined().catch((e: unknown) => {
    if (disposed) return;
    if (isForbiddenError(e)) {
      forbidden = true;
      printLine(red(t('terminal.forbidden')));
    } else {
      printLine(yellow(t('terminal.joinFailed', { message: hubErrorText(e) })));
    }
  });

  return {
    // xterm reflows wrapped lines on resize; redraw so the cursor math uses the new width.
    onResize: () => {
      if (promptShown && !busy) redraw();
    },
    dispose: () => {
      disposed = true;
      unsubscribe();
      inputDisposable.dispose();
    },
  };
}

// ---------------------------------------------------------------------------------------------

// SANDBOX_TERMINAL: переписано 2026-09-24 (ТЗ-2 §6, контракт C-8) — режим терминала выбирает
// сервер: «pty» (как раньше, только dev), «sandbox» (построчный ввод, команды идут в Docker-песочницу
// чата) или «unavailable» (Docker нет — показываем пояснение). Компонент монтируется заново на
// каждый чат (key={chatId} у родителя), а всё, что он создал, освобождается в cleanup.
/** Workspace terminal for one chat (xterm). */
export function TerminalPanel({ sessionId, autoFocus }: TerminalPanelProps) {
  const { t } = useTranslation();
  const effectiveTheme = useEffectiveTheme();
  const [phase, setPhase] = useState<Phase>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const containerRef = useRef<HTMLDivElement>(null);
  const termRef = useRef<Terminal | null>(null);
  // Long-lived terminal callbacks read the latest translator (the language can change meanwhile).
  const tRef = useRef<Translate>(t);
  tRef.current = t;
  const themeRef = useRef(effectiveTheme);
  themeRef.current = effectiveTheme;

  useEffect(() => {
    let cancelled = false;
    setPhase({ kind: 'loading' });
    getTerminalMode()
      .then((mode) => {
        if (cancelled) return;
        setPhase(mode === 'unavailable' ? { kind: 'unavailable' } : { kind: 'ready', mode });
      })
      .catch((e: unknown) => {
        if (!cancelled) setPhase({ kind: 'error', message: hubErrorText(e) });
      });
    return () => {
      cancelled = true;
    };
  }, [sessionId, attempt]);

  const readyMode = phase.kind === 'ready' ? phase.mode : null;

  useEffect(() => {
    const container = containerRef.current;
    if (!readyMode || !container) return;

    const term = new Terminal({
      theme: themeRef.current === 'light' ? LIGHT_THEME : DARK_THEME,
      fontSize: 13,
      cursorBlink: true,
      fontFamily: 'Consolas, "Cascadia Mono", Menlo, "DejaVu Sans Mono", "Courier New", monospace',
      scrollback: 5000,
      // Sandbox output is plain process output ("\n" line ends); a pty already sends "\r\n".
      convertEol: readyMode === 'sandbox',
    });
    const fitAddon = new FitAddon();
    term.loadAddon(fitAddon);
    term.open(container);
    termRef.current = term;

    const fit = () => {
      // Hidden (display: none) or collapsed: nothing to measure.
      if (container.clientWidth === 0 || container.clientHeight === 0) return;
      try {
        fitAddon.fit();
      } catch {
        // The renderer is not ready yet; the next resize will fit.
      }
    };
    fit();

    const session =
      readyMode === 'pty' ? startPtySession(term, sessionId, (k, o) => tRef.current(k, o)) : startSandboxSession(term, sessionId, (k, o) => tRef.current(k, o));
    const resizeDisposable = term.onResize(({ cols, rows }) => session.onResize(cols, rows));

    let frame = 0;
    const observer = new ResizeObserver(() => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(fit);
    });
    observer.observe(container);

    if (autoFocus) term.focus();

    return () => {
      cancelAnimationFrame(frame);
      observer.disconnect();
      resizeDisposable.dispose();
      session.dispose();
      termRef.current = null;
      term.dispose();
    };
    // autoFocus only matters for the first mount of a given chat's terminal.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [readyMode, sessionId]);

  // Follow the app theme without re-creating the terminal (that would drop the scrollback).
  useEffect(() => {
    const term = termRef.current;
    if (term) term.options.theme = effectiveTheme === 'light' ? LIGHT_THEME : DARK_THEME;
  }, [effectiveTheme]);

  return (
    <div className="terminal-dock__content">
      {phase.kind === 'loading' && <div className="terminal-notice">{t('terminal.connecting')}</div>}
      {phase.kind === 'unavailable' && (
        <div className="terminal-notice">
          <div className="terminal-notice__title">{t('terminal.unavailableTitle')}</div>
          <div>{t('terminal.unavailable')}</div>
        </div>
      )}
      {phase.kind === 'error' && (
        <div className="terminal-notice terminal-notice--error" role="alert">
          <div>{t('terminal.modeError', { message: phase.message })}</div>
          <button type="button" className="workspace__conflict-btn" onClick={() => setAttempt((a) => a + 1)}>
            {t('terminal.retry')}
          </button>
        </div>
      )}
      <div ref={containerRef} className="terminal-panel" hidden={phase.kind !== 'ready'} />
    </div>
  );
}
