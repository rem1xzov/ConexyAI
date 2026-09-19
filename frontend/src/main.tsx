import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './i18n';
import { applyTheme, getStoredTheme } from './theme';
import './styles/index.css';

applyTheme(getStoredTheme());

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
