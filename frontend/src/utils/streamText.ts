/**
 * STREAM_THROTTLE: helpers for rendering a live (streamed) answer without re-running the Markdown
 * parser on every frame.
 */

/**
 * How often a live answer re-runs the Markdown parser. Behind the parser sit KaTeX and mermaid, so
 * re-parsing the whole reply once per frame is what made streamed text stutter on a phone.
 */
export const STREAM_PARSE_INTERVAL_MS = 180;

/**
 * The part of a streaming answer that can be parsed as settled Markdown: whole lines only, so an
 * inline token (`**bold**`, a formula, a table row) is never cut in half. Falls back to the whole
 * text when there is no line break yet, so a single-line answer still gets formatted as it arrives.
 *
 * The result is always a prefix of `text`, which is what lets the caller append the remainder as
 * plain text (`text.slice(prefix.length)`).
 */
export function settledPrefix(text: string): string {
  const lastBreak = text.lastIndexOf('\n');
  return lastBreak >= 0 ? text.slice(0, lastBreak + 1) : text;
}

/**
 * STREAM_SANITIZER: на лету закрывает незавершённые инлайн-токены Markdown, чтобы во время
 * стриминга пользователь не видел сырые «**», «`» или «~~». Работает только с хвостом,
 * который ещё не отрендерен как финальный Markdown.
 */
export function closeStreamingInline(text: string): string {
  let out = text;

  // Незакрытый code fence: не даём звёздочкам внутри кода влиять на разметку.
  const fences = (out.match(/```/g) ?? []).length;
  if (fences % 2 === 1) return `${out}\n\`\`\``;

  const countToken = (token: string) => {
    let count = 0;
    let index = 0;
    while ((index = out.indexOf(token, index)) >= 0) {
      count += 1;
      index += token.length;
    }
    return count;
  };

  // Парные токены: если открыт, но не закрыт — закрываем в конце.
  for (const token of ['**', '~~']) {
    if (countToken(token) % 2 === 1) out += token;
  }

  // Одиночные звёздочки/подчёркивания (курсив), не входящие в **.
  const singles = (out.match(/(?<!\*)\*(?!\*)/g) ?? []).length;
  if (singles % 2 === 1) out += '*';
  const underscores = (out.match(/(?<!_)_(?!_)/g) ?? []).length;
  if (underscores % 2 === 1) out += '_';

  // Одиночный инлайн-код.
  const inlineTicks = (out.match(/(?<!`)`(?!`)/g) ?? []).length;
  if (inlineTicks % 2 === 1) out += '`';

  return out;
}

/**
 * Хвост, который ещё не прошёл Markdown-парсер, показываем как простой текст.
 * Убираем только незавершённый токен в самом конце, чтобы не мелькали звёздочки.
 */
export function sanitizeStreamingTail(text: string): string {
  return text.replace(/(\*{1,2}|~{1,2}|`{1,3}|_{1,2})$/, '');
}
