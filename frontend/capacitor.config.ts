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
  appId: 'ru.conexy.app',
  appName: 'ConexyAI',
  webDir: 'dist',
  server: {
    url: 'https://conexyai.ru',
    cleartext: false,
    androidScheme: 'https',
    // OAUTH_IN_WEBVIEW: вход через GitHub уводит на github.com. Без этого списка WebView отдаёт
    // чужой домен системному браузеру — сессия остаётся там, и приложение возвращается неавторизованным.
    allowNavigation: ['conexyai.ru', 'www.conexyai.ru', 'github.com', '*.github.com'],
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
