import { useSyncExternalStore } from 'react';
import http from '../api/client';
import { getNativePlugin, listenForAppState } from '../utils/mobile';
import {
  CallDedupe,
  errorToOutcome,
  normalizeToolOutcome,
  parsePhoneToolCall,
  withTimeout,
  type PhoneToolCall,
  type PhoneToolOutcome,
} from '../utils/phoneTool';
import { signalrService } from './signalrService';

// PHONE_AGENT: мост «серверный агент → нативный плагин PhoneAgent». Контракт — frontend/PHONE_AGENT.md.
//
//   агент (сервер) ──PhoneTool──▶ этот модуль ──execute──▶ PhoneAgentPlugin (Java)
//                ◀─PhoneToolResult──                 ◀──{output,isError}──
//
// Всё здесь — no-op, пока плагина нет (сайт, старые APK): isPhoneAgentAvailable() === false.
// Второго SignalR-соединения не создаём: подписываемся на общее в signalrService.

export type PermissionState = 'granted' | 'denied' | 'prompt' | 'unavailable';

export const PHONE_PERMISSION_IDS = [
  'camera',
  'microphone',
  'photos',
  'videos',
  'audio',
  'notifications',
  'contacts',
  'location',
  'usage_access',
  'all_files',
  'accessibility',
  'battery_optimization',
  'install_packages',
  'overlay',
  'write_settings',
] as const;

export type PhonePermissionId = (typeof PHONE_PERMISSION_IDS)[number];
export type PhonePermissions = Record<string, PermissionState | string>;

export interface PhoneInfo {
  model?: string;
  manufacturer?: string;
  androidVersion?: string;
  sdk?: number | string;
  appVersion?: string;
}

export interface PhoneNotificationTarget {
  type?: string;
  taskId?: string;
  chatId?: string;
  actionId?: string;
}

interface ListenerHandle {
  remove: () => Promise<void> | void;
}

interface PhoneAgentPlugin {
  execute: (options: { tool: string; args: string }) => Promise<{ output: string; isError: boolean }>;
  info: () => Promise<PhoneInfo>;
  permissions: () => Promise<{ permissions: PhonePermissions }>;
  requestPermission: (options: { permission: string }) => Promise<{ state: string }>;
  requestAll: () => Promise<{ permissions: PhonePermissions }>;
  // SERVER_ORIGIN: адрес сервера зашит в плагине, baseUrl больше не передаётся.
  setAuth: (options: { token: string | null }) => Promise<unknown>;
  /** NATIVE_AGENT_SWITCH: нативная копия выключателя; в старых APK метода нет. */
  setEnabled?: (options: { enabled: boolean }) => Promise<unknown>;
  getPushToken: () => Promise<{ token: string | null }>;
  showNotification: (options: {
    id?: number;
    title: string;
    body: string;
    type?: 'confirm' | 'completed' | 'phone_needed';
    actionId?: string;
    taskId?: string;
    chatId?: string;
    dangerous?: boolean;
  }) => Promise<unknown>;
  cancelNotification: (options: { actionId?: string; taskId?: string }) => Promise<unknown>;
  getLaunchNotification: () => Promise<PhoneNotificationTarget>;
  addListener: (event: string, callback: (data: never) => void) => Promise<ListenerHandle> | ListenerHandle;
}

const PLUGIN_NAME = 'PhoneAgent';
const ENABLED_KEY = 'conexy.phoneAgent.enabled';
const ONBOARDED_KEY = 'conexy.phoneAgent.onboarded';

/** Обычный инструмент должен уложиться в это время, иначе в ответ уходит ошибка-таймаут. */
const TOOL_TIMEOUT_MS = 120_000;
/** Инструменты, которые ждут пользователя (системный диалог, галерея): даём больше времени. */
const INTERACTIVE_TOOL_TIMEOUT_MS = 300_000;
const INTERACTIVE_TOOLS = new Set(['phone_request_permission', 'phone_gallery_pick']);

function plugin(): PhoneAgentPlugin | null {
  return getNativePlugin<PhoneAgentPlugin>(PLUGIN_NAME);
}

/** True только в нативном Android-приложении, где зарегистрирован плагин PhoneAgent. */
export function isPhoneAgentAvailable(): boolean {
  return plugin() !== null;
}

// ── настройка «Агент телефона» ────────────────────────────────────────────────────────────────

function readEnabled(): boolean {
  try {
    return localStorage.getItem(ENABLED_KEY) !== '0'; // по умолчанию включено
  } catch {
    return true;
  }
}

let enabled = readEnabled();

export function isPhoneAgentEnabled(): boolean {
  return enabled;
}

export function setPhoneAgentEnabled(value: boolean): void {
  enabled = value;
  try {
    localStorage.setItem(ENABLED_KEY, value ? '1' : '0');
  } catch {
    // хранилище может быть недоступно
  }
  syncNativeEnabled();
  emit();
}

/** NATIVE_AGENT_SWITCH: плагин сам отказывает в execute, когда агент выключен, — держим флаг в синхроне. */
function syncNativeEnabled(): void {
  const native = plugin();
  if (!native?.setEnabled) return;
  void Promise.resolve(native.setEnabled({ enabled })).catch(() => undefined);
}

/** Слать ли `usePhone: true` в запросе задачи. */
export function shouldUsePhone(): boolean {
  return isPhoneAgentAvailable() && enabled;
}

export function isPhoneOnboardingPending(): boolean {
  if (!isPhoneAgentAvailable()) return false;
  try {
    return localStorage.getItem(ONBOARDED_KEY) !== '1';
  } catch {
    return false;
  }
}

export function markPhoneOnboarded(): void {
  try {
    localStorage.setItem(ONBOARDED_KEY, '1');
  } catch {
    // ignore
  }
}

// ── состояние для UI ──────────────────────────────────────────────────────────────────────────

export interface PhoneAgentSnapshot {
  available: boolean;
  enabled: boolean;
  /** Телефон зарегистрирован на сервере (RegisterPhone вернул true на текущем соединении). */
  registered: boolean;
  info: PhoneInfo | null;
  permissions: PhonePermissions;
}

let snapshot: PhoneAgentSnapshot = {
  available: false,
  enabled,
  registered: false,
  info: null,
  permissions: {},
};
const stateListeners = new Set<() => void>();

function patchState(patch: Partial<PhoneAgentSnapshot>): void {
  snapshot = { ...snapshot, ...patch, enabled };
  stateListeners.forEach((l) => l());
}

function emit(): void {
  patchState({});
}

function subscribe(listener: () => void): () => void {
  stateListeners.add(listener);
  return () => stateListeners.delete(listener);
}

function getSnapshot(): PhoneAgentSnapshot {
  return snapshot;
}

export function usePhoneAgentState(): PhoneAgentSnapshot {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}

// ── выполнение инструментов ───────────────────────────────────────────────────────────────────

const dedupe = new CallDedupe();

async function sendResult(callId: string, outcome: PhoneToolOutcome): Promise<void> {
  // Результат обязателен: ответ не усекаем (IMAGE_BASE64 бывает огромным) и повторяем отправку,
  // пока хаб переподключается, — иначе серверный агент завис бы до собственного таймаута.
  let lastError: unknown;
  for (let attempt = 0; attempt < 5; attempt++) {
    try {
      await signalrService.invokeHub<boolean>('PhoneToolResult', callId, outcome.output, outcome.isError);
      return;
    } catch (error) {
      lastError = error;
      await new Promise((resolve) => window.setTimeout(resolve, 1000 * (attempt + 1)));
    }
  }
  console.warn('[phone] PhoneToolResult failed', callId, lastError);
}

async function runTool(call: PhoneToolCall): Promise<PhoneToolOutcome> {
  const native = plugin();
  if (!native) return { output: 'Плагин телефона недоступен в этой версии приложения', isError: true };
  if (!enabled) return { output: 'Агент телефона выключен в настройках приложения', isError: true };
  const timeout = INTERACTIVE_TOOLS.has(call.tool) ? INTERACTIVE_TOOL_TIMEOUT_MS : TOOL_TIMEOUT_MS;
  const execution = native.execute({ tool: call.tool, args: call.args }).then(normalizeToolOutcome);
  return withTimeout(execution, timeout, call.tool);
}

async function handlePhoneTool(raw: unknown): Promise<void> {
  const call = parsePhoneToolCall(raw);
  if (!call) {
    console.warn('[phone] PhoneTool without callId ignored', raw);
    return;
  }
  // Один callId — один ответ: повторная доставка события после реконнекта игнорируется.
  if (!dedupe.claim(call.callId)) return;

  let outcome: PhoneToolOutcome;
  try {
    outcome = await runTool(call);
  } catch (error) {
    outcome = errorToOutcome(error);
  }
  await sendResult(call.callId, outcome);
}

// ── регистрация на сервере ────────────────────────────────────────────────────────────────────

let registerInFlight: Promise<void> | null = null;
let registerAgain = false;

async function readPhoneProfile(native: PhoneAgentPlugin): Promise<{ info: PhoneInfo | null; permissions: PhonePermissions }> {
  const [info, perms] = await Promise.all([
    native.info().catch(() => null),
    native.permissions().then((r) => r.permissions).catch(() => ({}) as PhonePermissions),
  ]);
  return { info, permissions: perms };
}

/** RegisterPhone: после каждого (пере)подключения хаба и при смене разрешений. Склеивает частые вызовы. */
export function registerPhone(): Promise<void> {
  if (registerInFlight) {
    registerAgain = true;
    return registerInFlight;
  }
  registerInFlight = (async () => {
    try {
      do {
        registerAgain = false;
        const native = plugin();
        if (!native) return;
        const { info, permissions } = await readPhoneProfile(native);
        patchState({ info: info ?? snapshot.info, permissions });
        if (!signalrService.isConnected) return;
        const payload = JSON.stringify({
          model: info?.model ?? '',
          manufacturer: info?.manufacturer ?? '',
          androidVersion: info?.androidVersion ?? '',
          sdk: info?.sdk ?? 0,
          appVersion: info?.appVersion ?? '',
          permissions,
        });
        try {
          const ok = await signalrService.invokeIfConnected<boolean>('RegisterPhone', payload);
          patchState({ registered: ok !== false });
          await reportForeground(true);
        } catch (error) {
          patchState({ registered: false });
          console.warn('[phone] RegisterPhone failed', error);
        }
      } while (registerAgain);
    } finally {
      registerInFlight = null;
    }
  })();
  return registerInFlight;
}

let lastForeground: boolean | null = null;

async function reportForeground(force = false): Promise<void> {
  const foreground = document.visibilityState !== 'hidden' && appActive;
  if (!force && foreground === lastForeground) return;
  lastForeground = foreground;
  try {
    await signalrService.invokeIfConnected('SetPhoneForeground', foreground);
  } catch {
    lastForeground = null; // не дошло — при следующем подключении отправим заново
  }
}

let appActive = true;

// ── разрешения (для UI) ───────────────────────────────────────────────────────────────────────

export async function refreshPhonePermissions(): Promise<PhonePermissions> {
  const native = plugin();
  if (!native) return {};
  const permissions = await native.permissions().then((r) => r.permissions);
  patchState({ permissions });
  return permissions;
}

export async function requestPhonePermission(permission: string): Promise<string> {
  const native = plugin();
  if (!native) return 'unavailable';
  try {
    const { state } = await native.requestPermission({ permission });
    return state;
  } finally {
    void refreshPhonePermissions().then(() => registerPhone()).catch(() => undefined);
  }
}

export async function requestAllPhonePermissions(): Promise<PhonePermissions> {
  const native = plugin();
  if (!native) return {};
  try {
    const { permissions } = await native.requestAll();
    patchState({ permissions });
    return permissions;
  } finally {
    void registerPhone();
  }
}

export async function sendTestPhoneNotification(title: string, body: string): Promise<boolean> {
  const native = plugin();
  if (!native) return false;
  try {
    await native.showNotification({ id: 9001, title, body, type: 'completed' });
    return true;
  } catch {
    return false;
  }
}

/** Убрать уведомление, когда решение принято в самом приложении (карточка в чате). */
export function cancelPhoneNotification(target: { actionId?: string; taskId?: string }): void {
  const native = plugin();
  if (!native) return;
  void Promise.resolve(native.cancelNotification(target)).catch(() => undefined);
}

// ── авторизация и push-токен ──────────────────────────────────────────────────────────────────

let pushToken: string | null = null;
let registeredPushKey: string | null = null;
let currentAuth: string | null = null;

async function syncPushToken(): Promise<void> {
  if (!pushToken || !currentAuth) return;
  const key = `${currentAuth}\u0000${pushToken}`;
  if (registeredPushKey === key) return;
  try {
    await http.post('/mobile/push-token', { token: pushToken, platform: 'android' });
    registeredPushKey = key;
  } catch (error) {
    console.warn('[phone] push-token registration failed', error);
  }
}

/**
 * Передаёт плагину JWT и адрес сервера (кнопки уведомления отвечают серверу сами); null — выход.
 * Вызывать ПОСЛЕ setAuthToken: регистрация push-токена идёт через общий http-клиент.
 */
export function setPhoneAuth(token: string | null): void {
  const native = plugin();
  if (!native) return;
  currentAuth = token;
  void Promise.resolve(native.setAuth({ token })).catch(() => undefined);
  if (token) void syncPushToken();
  else registeredPushKey = null;
}

/** Выход из аккаунта: снять push-токен на сервере (пока JWT ещё действует) и обнулить auth плагина. */
export async function phoneAgentSignOut(token: string | null): Promise<void> {
  const native = plugin();
  if (!native) return;
  try {
    if (pushToken && token) {
      await http.delete('/mobile/push-token', {
        data: { token: pushToken },
        headers: { Authorization: `Bearer ${token}` },
      });
    }
  } catch (error) {
    console.warn('[phone] push-token removal failed', error);
  }
  registeredPushKey = null;
  currentAuth = null;
  patchState({ registered: false });
  void Promise.resolve(native.setAuth({ token: null })).catch(() => undefined);
}

// ── нажатие на уведомление ────────────────────────────────────────────────────────────────────

type OpenHandler = (target: PhoneNotificationTarget) => void;
let openHandler: OpenHandler | null = null;
let pendingOpen: PhoneNotificationTarget | null = null;

function deliverOpen(target: PhoneNotificationTarget | null | undefined): void {
  if (!target?.chatId) return;
  if (openHandler) openHandler(target);
  else pendingOpen = target;
}

/** Регистрирует обработчик тапа по уведомлению; если тап был раньше (холодный старт) — вызывает сразу. */
export function setPhoneNotificationHandler(handler: OpenHandler | null): void {
  openHandler = handler;
  if (handler && pendingOpen) {
    const target = pendingOpen;
    pendingOpen = null;
    handler(target);
  }
}

// ── запуск ────────────────────────────────────────────────────────────────────────────────────

let started = false;

/** Однократная инициализация: подписки на плагин, приложение и общий хаб. На сайте — no-op. */
export function startPhoneAgent(): void {
  if (started) return;
  const native = plugin();
  if (!native) return;
  started = true;
  patchState({ available: true });
  // NATIVE_AGENT_SWITCH: миграция — переносим текущую настройку из localStorage в плагин при каждом старте.
  syncNativeEnabled();

  const listen = (event: string, callback: (data: never) => void) => {
    try {
      void Promise.resolve(native.addListener(event, callback)).catch(() => undefined);
    } catch {
      // старый плагин без этого события — не критично
    }
  };

  signalrService.onPhoneTool((payload) => void handlePhoneTool(payload));
  signalrService.onConnected(() => void registerPhone());
  signalrService.onStateChange((state) => {
    if (state !== 'connected') {
      lastForeground = null;
      if (snapshot.registered) patchState({ registered: false });
    }
  });

  listen('permissionsChanged', ((data: { permissions?: PhonePermissions }) => {
    if (data?.permissions) patchState({ permissions: data.permissions });
    void registerPhone();
  }) as (data: never) => void);

  listen('pushToken', ((data: { token?: string | null }) => {
    if (data?.token) {
      pushToken = data.token;
      void syncPushToken();
    }
  }) as (data: never) => void);

  listen('notificationOpened', ((data: PhoneNotificationTarget) => deliverOpen(data)) as (data: never) => void);

  void native.getPushToken().then(({ token }) => {
    if (token) {
      pushToken = token;
      void syncPushToken();
    }
  }, () => undefined);

  void native.getLaunchNotification().then((target) => deliverOpen(target), () => undefined);

  void listenForAppState((active) => {
    appActive = active;
    void reportForeground();
  });
  document.addEventListener('visibilitychange', () => void reportForeground());

  void refreshPhonePermissions().catch(() => undefined);
  void native.info().then((info) => patchState({ info }), () => undefined);
}
