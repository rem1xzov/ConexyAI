import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './i18n';
import { applyTheme, getStoredTheme } from './theme';
import { applyDisplayPrefs } from './utils/displayPrefs';
import { ErrorBoundary } from './components/ErrorBoundary';
import { ArtifactPanelHost } from './components/artifacts/ArtifactPanel';
import { checkMobileUpdate, listenForMobileAuth, showUpdateBanner } from './utils/mobile';
import './styles/index.css';

applyTheme(getStoredTheme());
applyDisplayPrefs();

// MOBILE_HANDOFF: приложение вернулось из браузера с одноразовым кодом — перезагружаем SPA на
// адрес с кодом, и useAuth обменяет его на сессию.
listenForMobileAuth((code) => {
  window.location.replace(`/?handoff=${encodeURIComponent(code)}`);
});

// AUTO_UPDATE: как в десктопном клиенте — проверка последнего релиза на старте.
void checkMobileUpdate().then((update) => {
  if (update) showUpdateBanner(update);
});

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
