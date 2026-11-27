// VISUAL_DESKTOP: визуальный десктоп консольного клиента — тот же самый веб-клиент, который
// консоль открывает по пути `/desktop` в своём отдельном окне Chromium (Edge/Chrome `--app`).
// Уникальный заголовок документа нужен, чтобы клиент находил и фокусировал ровно своё окно по
// заголовку, а публичные ссылки «Поделиться» должны по-прежнему вести на обычный корень сайта,
// а не на служебный `/desktop`.
export const VISUAL_DESKTOP_TITLE = 'ConexyAI Desktop';

/** Убирает завершающие слеши, чтобы `/desktop/`, `/desktop//` и `/desktop` считались одним путём. */
function normalizePath(pathname: string): string {
  const trimmed = pathname.replace(/\/+$/, '');
  return trimmed === '' ? '/' : trimmed;
}

/** true, если приложение открыто в окне визуального десктопа (путь `/desktop`). */
export function isVisualDesktopPath(pathname: string): boolean {
  return normalizePath(pathname) === '/desktop';
}

/** Публичный путь для ссылок: служебный `/desktop` превращаем в корень сайта, остальное не трогаем. */
export function publicPath(pathname: string): string {
  return isVisualDesktopPath(pathname) ? '/' : pathname;
}
