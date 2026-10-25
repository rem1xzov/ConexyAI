// MOBILE_APP: всё, что нужно веб-клиенту знать про Android-обёртку (Capacitor).
// Мост `window.Capacitor` внедряется нативно, поэтому веб-сборка работает без этих пакетов.

interface CapacitorPlugins {
  Browser?: {
    open: (options: { url: string }) => Promise<void>;
  };
  App?: {
    addListener: (event: 'appUrlOpen', callback: (data: { url: string }) => void) => Promise<unknown> | unknown;
    getInfo: () => Promise<{ version: string; build: string }>;
  };
  // APK_UPDATER: локальный нативный плагин приложения, на сайте его нет.
  ApkUpdater?: {
    downloadAndInstall: (options: { url: string }) => Promise<{ started: boolean }>;
    addListener: (
      event: 'updateFailed' | 'updateInstalling',
      callback: (data: { reason?: string; ok?: boolean }) => void,
    ) => Promise<unknown> | unknown;
  };
  // VOICE_INPUT: нативное распознавание речи для Android (в WebView нет Web Speech API).
  SpeechInput?: {
    start: (options?: { lang?: string }) => Promise<void>;
    stop: () => Promise<void>;
    cancel: () => Promise<void>;
    addListener: (
      event: 'result' | 'error' | 'end',
      callback: (data: { text?: string; isFinal?: boolean; code?: string }) => void,
    ) => Promise<{ remove: () => Promise<void> }> | { remove: () => Promise<void> };
  };
}

interface CapacitorBridge {
  isNativePlatform?: () => boolean;
  getPlatform?: () => string;
  Plugins?: CapacitorPlugins;
}

const APP_SCHEME = 'ru.conexy.app://auth';
const MOBILE_LOGIN_URL = '/api/auth/mobile?redirect=ru.conexy.app://auth';
const APK_ASSET = 'conexy-android.apk';
const RELEASES_API = 'https://api.github.com/repos/rem1xzov/ConexyAI/releases?per_page=20';

function bridge(): CapacitorBridge | undefined {
  return (window as unknown as { Capacitor?: CapacitorBridge }).Capacitor;
}

/** True only inside the Android shell (the website itself reports false). */
export function isMobileApp(): boolean {
  return Boolean(bridge()?.isNativePlatform?.());
}

/**
 * VOICE_INPUT: в Android WebView нет `SpeechRecognition`, поэтому в приложении распознаёт
 * нативный Android. На сайте возвращает null — там работает браузерное API.
 */
export type NativeSpeech = NonNullable<CapacitorPlugins['SpeechInput']>;

export function nativeSpeech(): NativeSpeech | null {
  return bridge()?.Plugins?.SpeechInput ?? null;
}

/**
 * MOBILE_HANDOFF: OAuth открывается в системном браузере. Google запрещает вход внутри WebView
 * («This browser or app may not be secure»), поэтому GitHub→Google в приложении не работал.
 * Браузер сам вернёт сессию приложению через `ru.conexy.app://auth`.
 */
export async function openMobileLogin(): Promise<boolean> {
  const browser = bridge()?.Plugins?.Browser;
  if (!browser) return false;
  await browser.open({ url: new URL(MOBILE_LOGIN_URL, window.location.origin).toString() });
  return true;
}

/** Registers the deep-link listener; returns false when the app shell is absent. */
export function listenForMobileAuth(onCode: (code: string) => void): boolean {
  const app = bridge()?.Plugins?.App;
  if (!app) return false;
  void app.addListener('appUrlOpen', (event) => {
    if (!event.url.startsWith(APP_SCHEME)) return;
    try {
      const code = new URL(event.url).searchParams.get('code');
      if (code) onCode(code);
    } catch {
      // Мусорный deep link игнорируем: приложение просто останется в текущем состоянии.
    }
  });
  return true;
}

export interface AppUpdate {
  version: string;
  url: string;
}

function isNewer(candidate: string, current: string): boolean {
  const a = candidate.split('.').map((part) => Number.parseInt(part, 10) || 0);
  const b = current.split('.').map((part) => Number.parseInt(part, 10) || 0);
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    const left = a[i] ?? 0;
    const right = b[i] ?? 0;
    if (left !== right) return left > right;
  }
  return false;
}

/**
 * AUTO_UPDATE: как в десктопном клиенте — при запуске ищем свежий релиз на GitHub.
 * Приложение и десктоп выпускаются независимо, поэтому берём не просто последний релиз,
 * а первый по времени, в котором есть APK: релиз только с exe — не наше обновление.
 */
export async function checkMobileUpdate(): Promise<AppUpdate | null> {
  const app = bridge()?.Plugins?.App;
  if (!app) return null;
  try {
    const info = await app.getInfo();
    const response = await fetch(RELEASES_API, { headers: { Accept: 'application/vnd.github+json' } });
    if (!response.ok) return null;
    const releases = (await response.json()) as {
      tag_name?: string;
      draft?: boolean;
      assets?: { name?: string; browser_download_url?: string }[];
    }[];
    if (!Array.isArray(releases)) return null;
    for (const release of releases) {
      if (release.draft) continue;
      const version = (release.tag_name ?? '').replace(/^v/, '');
      const asset = release.assets?.find((item) => item.name === APK_ASSET);
      if (!version || !asset?.browser_download_url) continue;
      return isNewer(version, info.version) ? { version, url: asset.browser_download_url } : null;
    }
    return null;
  } catch {
    return null;
  }
}

/** Полоса обновления: ненавязчивая, закрывается, ставит APK через нативный плагин. */
/**
 * APK_UPDATER: отдаём ссылку нативному плагину — он скачивает APK через DownloadManager
 * (прогресс в шторке) и сам открывает системный установщик. Раньше здесь открывался Custom Tab,
 * и Chrome бесконечно грузил страницу вместо установки файла.
 */
function installWithPlugin(
  updater: NonNullable<CapacitorPlugins['ApkUpdater']>,
  update: AppUpdate,
  button: HTMLButtonElement,
): void {
  const idleLabel = button.textContent ?? 'Обновить';
  button.disabled = true;
  button.style.opacity = '0.7';
  button.textContent = 'Загрузка…';

  const reset = (label: string) => {
    button.disabled = false;
    button.style.opacity = '1';
    button.textContent = label;
  };

  void updater.addListener('updateFailed', () => reset(idleLabel));

  updater.downloadAndInstall({ url: update.url }).catch((error: unknown) => {
    const code = (error as { code?: string } | undefined)?.code;
    reset(code === 'INSTALL_PERMISSION_DENIED' ? 'Разрешить установку' : 'Повторить');
  });
}

export function showUpdateBanner(update: AppUpdate): void {
  if (document.getElementById('conexy-update')) return;

  const bar = document.createElement('div');
  bar.id = 'conexy-update';
  bar.setAttribute('role', 'status');
  bar.style.cssText = [
    'position:fixed', 'left:12px', 'right:12px', 'bottom:calc(12px + env(safe-area-inset-bottom))',
    'z-index:2147483000', 'display:flex', 'align-items:center', 'gap:10px',
    'padding:10px 12px', 'border-radius:12px', 'background:#1b1d24', 'color:#f2f4f8',
    'font:14px/1.35 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif',
    'box-shadow:0 8px 24px rgba(0,0,0,.35)',
  ].join(';');

  const text = document.createElement('span');
  text.textContent = `Доступно обновление ${update.version}`;
  text.style.cssText = 'flex:1 1 auto';

  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = 'Обновить';
  button.style.cssText = [
    'flex:0 0 auto', 'border:0', 'border-radius:9px', 'padding:8px 14px', 'min-height:36px',
    'background:#0e7490', 'color:#fff', 'font:inherit', 'font-weight:600', 'cursor:pointer',
  ].join(';');
  button.addEventListener('click', () => {
    const updater = bridge()?.Plugins?.ApkUpdater;
    if (updater) {
      installWithPlugin(updater, update, button);
      return;
    }
    // Сайт и старые сборки: там нативного установщика нет, остаётся скачивание браузером.
    const browser = bridge()?.Plugins?.Browser;
    if (browser) void browser.open({ url: update.url });
    else window.location.href = update.url;
  });

  const close = document.createElement('button');
  close.type = 'button';
  close.setAttribute('aria-label', 'Закрыть');
  close.textContent = '✕';
  close.style.cssText = [
    'flex:0 0 auto', 'border:0', 'background:transparent', 'color:#9aa4b2',
    'font:inherit', 'cursor:pointer', 'padding:4px 6px', 'min-height:36px',
  ].join(';');
  close.addEventListener('click', () => bar.remove());

  bar.append(text, button, close);
  document.body.appendChild(bar);
}
