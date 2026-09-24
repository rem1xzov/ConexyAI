import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './i18n';
import { applyTheme, getStoredTheme } from './theme';
import { ErrorBoundary } from './components/ErrorBoundary';
import { ArtifactPanelHost } from './components/artifacts/ArtifactPanel';
import './styles/index.css';

applyTheme(getStoredTheme());

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
