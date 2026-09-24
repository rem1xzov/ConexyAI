import { signalrService } from './signalrService';

// SANDBOX_TERMINAL: добавлено 2026-09-24 — клиентская сторона контракта C-8.
// Терминал воркспейса работает в одном из трёх режимов, который выбирает сервер:
//  - "pty"         — интерактивный шелл на самом бэкенде (только dev: StartTerminal/SendInput/...);
//  - "sandbox"     — построчный режим: каждая команда выполняется в Docker-песочнице этого чата
//                    (тот же контейнер и та же /workspace, что у агента), вывод приходит событиями
//                    TerminalOutput с source "user";
//  - "unavailable" — Docker недоступен, терминала нет.

export type TerminalMode = 'pty' | 'sandbox' | 'unavailable';

export interface SandboxCommandResult {
  exitCode: number;
  timedOut: boolean;
  /** "busy" when another command of this chat is still running; any other text is a failure. */
  error: string | null;
}

// The mode is a property of the server, not of a chat: remember it briefly so reopening the
// terminal or switching chats does not ask again every time.
const MODE_TTL_MS = 30_000;
let cachedMode: { mode: TerminalMode; at: number } | null = null;
let modeInFlight: Promise<TerminalMode> | null = null;

function normalizeMode(raw: unknown): TerminalMode {
  const value = typeof raw === 'string' ? raw.trim().toLowerCase() : '';
  return value === 'pty' || value === 'sandbox' ? value : 'unavailable';
}

export function getTerminalMode(): Promise<TerminalMode> {
  if (cachedMode && Date.now() - cachedMode.at < MODE_TTL_MS) return Promise.resolve(cachedMode.mode);
  if (!modeInFlight) {
    modeInFlight = signalrService
      .invokeHub<string>('GetTerminalMode')
      .then((raw) => {
        const mode = normalizeMode(raw);
        cachedMode = { mode, at: Date.now() };
        return mode;
      })
      .finally(() => {
        modeInFlight = null;
      });
  }
  return modeInFlight;
}

/** Runs one command in the chat's sandbox; resolves when it finishes (server timeout: 120 s). */
export async function runSandboxCommand(chatId: string, command: string): Promise<SandboxCommandResult> {
  const raw = await signalrService.invokeHub<Partial<SandboxCommandResult> | null>('RunSandboxCommand', chatId, command);
  return {
    exitCode: typeof raw?.exitCode === 'number' ? raw.exitCode : -1,
    timedOut: raw?.timedOut === true,
    error: typeof raw?.error === 'string' && raw.error ? raw.error : null,
  };
}

/** Asks the server to stop the chat's running sandbox command; false when nothing was running. */
export async function cancelSandboxCommand(chatId: string): Promise<boolean> {
  const res = await signalrService.invokeHub<boolean>('CancelSandboxCommand', chatId);
  return res === true;
}

/** True when the hub refused the call because the chat belongs to another user (contract C-6). */
export function isForbiddenError(err: unknown): boolean {
  const message = err instanceof Error ? err.message : String(err ?? '');
  return /forbidden/i.test(message);
}

/** Short, single-line text of a hub error (SignalR prefixes server errors with boilerplate). */
export function hubErrorText(err: unknown): string {
  const message = err instanceof Error ? err.message : String(err ?? '');
  const cleaned = message
    .replace(/^An unexpected error occurred invoking '[^']*' on the server\.\s*/i, '')
    .replace(/^HubException:\s*/i, '')
    .replace(/\s+/g, ' ')
    .trim();
  return cleaned.length > 200 ? `${cleaned.slice(0, 200)}…` : cleaned;
}
