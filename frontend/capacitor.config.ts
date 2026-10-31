import type { CapacitorConfig } from '@capacitor/cli';

/**
 * MOBILE_SHELL: Android-обёртка вокруг веб-клиента.
 *
 * Первая версия грузит прод-сайт (`server.url`), а не локальные ассеты: так cookie сессии,
 * авторизация, SignalR и загрузка файлов остаются same-origin, и сервер менять не нужно.
 * Локальные ассеты (мгновенный старт, работа без сети на старте) включаются, когда в CORS
 * сервера появится origin приложения — см. `frontend/MOBILE.md`.
 */
const config: CapacitorConfig = {
  appId: 'conexy.app',
  appName: 'ConexyAI',
  webDir: 'dist',
  server: {
    url: 'https://conexyai.ru',
    cleartext: false,
    androidScheme: 'https',
    // BRIDGE_ORIGIN_GUARD: только наш домен. Хосты из этого списка получают нативный мост, поэтому
    // github.com здесь нет: OAuth идёт в системном браузере с возвратом по conexy.app://auth,
    // а APK обновлений качает DownloadManager (ApkUpdater) — WebView туда не переходит.
    allowNavigation: ['conexyai.ru', 'www.conexyai.ru'],
  },
  android: {
    allowMixedContent: false,
    webContentsDebuggingEnabled: false,
  },
  plugins: {
    SplashScreen: {
      launchShowDuration: 700,
      backgroundColor: '#0b0f14',
      showSpinner: false,
    },
  },
};

export default config;
