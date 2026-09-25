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
