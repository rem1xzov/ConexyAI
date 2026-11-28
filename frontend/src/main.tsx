import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './i18n';
import { applyTheme, getStoredTheme } from './theme';
import { applyDisplayPrefs } from './utils/displayPrefs';
import { ErrorBoundary } from './components/ErrorBoundary';
import { ArtifactPanelHost } from './components/artifacts/ArtifactPanel';
import { checkMobileUpdate, listenForMobileAuth, showUpdateBanner } from './utils/mobile';
import { startPhoneAgent } from './services/phoneAgent';
import { installMotionVars } from './motion/motionConfig';
// VISUAL_DESKTOP: окно визуального десктопа ищем по уникальному заголовку документа.
import { isVisualDesktopPath, VISUAL_DESKTOP_TITLE } from './utils/desktopMode';
import './styles/index.css';

installMotionVars();
applyTheme(getStoredTheme());
applyDisplayPrefs();

// MOBILE_HANDOFF: приложение вернулось из браузера с одноразовым кодом — перезагружаем SPA на
// адрес с кодом, и useAuth обменяет его на сессию.
listenForMobileAuth((code) => {
  window.location.replace(`/?handoff=${encodeURIComponent(code)}`);
});

// PHONE_AGENT: мост к плагину PhoneAgent. Запускаем до монтирования App, чтобы не потерять тап по
// уведомлению, открывший приложение с холодного старта. На сайте — no-op.
startPhoneAgent();

// AUTO_UPDATE: как в десктопном клиенте — проверка последнего релиза на старте.
void checkMobileUpdate().then((update) => {
  if (update) showUpdateBanner(update);
});

// VISUAL_DESKTOP: консольный клиент открывает этот же SPA по `/desktop` в своём окне Chromium
// app-mode и находит это окно по заголовку, поэтому заголовок ставим до первого рендера.
if (isVisualDesktopPath(window.location.pathname)) {
  document.title = VISUAL_DESKTOP_TITLE;
  // VISUAL_DESKTOP: атрибут включает «плотную» тему окна (без стеклянных размытий и цветных
  // пятен) — визуально как референс DeepSeek Harness; обычный сайт и приложение его не видят.
  // Тема включается в App только для вкладки агента; здесь помечаем само окно (общие правила окна).
  document.documentElement.dataset.visualDesktopWindow = 'true';
}

// GLOBAL_ERROR_BOUNDARY: добавлено 2026-09-23
// Обёртка стоит на самом корне, а не внутри App: только так она ловит и ошибку самого App
// (например, при инициализации), иначе любой неперехваченный рендер-краш размонтирует всё дерево и
// пользователь видит пустой тёмный экран без объяснений. Здесь вместо этого — окно восстановления
// с кнопками «Перезагрузить» и «Сбросить сессию».
ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ErrorBoundary recovery>
      <App />
      {/* ARTIFACTS: добавлено 2026-09-24 — правая панель артефактов живёт рядом с App (портал в
          body) и управляется своим маленьким стором, поэтому App.tsx о ней не знает. */}
      <ArtifactPanelHost />
    </ErrorBoundary>
  </React.StrictMode>,
);
