import { memo, useEffect, useMemo, useRef, useState } from 'react';
import { splitArtifacts } from '../../utils/artifacts';
import {
  settledPrefix,
  STREAM_PARSE_INTERVAL_MS,
  closeStreamingInline,
  sanitizeStreamingTail,
  plainStreamTail,
} from '../../utils/streamText';
import { useSmoothStream } from '../../hooks/useSmoothStream';
import { Markdown } from '../Markdown';
import { ArtifactCard } from './ArtifactCard';
import { getArtifactState, openArtifact, registerMessageArtifacts, unregisterMessage } from './store';

interface AssistantContentProps {
  messageId: string;
  createdAt: number;
  content: string;
  streaming: boolean;
}

/** Artifacts already auto-opened in this tab, so a finished one never pops the panel again. */
const autoOpened = new Set<string>();

function isWideScreen(): boolean {
  return typeof window.matchMedia === 'function' && window.matchMedia('(min-width: 1024px)').matches;
}

/**
 * TOKEN_FADE: последнее слово хвоста получает плавное проявление; уже показанные слова
 * остаются стабильными. История и завершённые ответы не анимируются вовсе.
 */
function StreamingTail({ text }: { text: string }) {
  const lastBreak = Math.max(text.lastIndexOf(' '), text.lastIndexOf('\n'));
  const stable = text.slice(0, lastBreak + 1);
  const word = text.slice(lastBreak + 1);
  return (
    <>
      {stable}
      {word && (
        <span className="stream-word" key={stable.length}>
          {word}
        </span>
      )}
    </>
  );
}

/**
 * ARTIFACTS: добавлено 2026-09-24 — текст ответа ассистента: Markdown между тегами
 * <conexy_artifact> и карточки артефактов на месте самих тегов.
 *
 * Артефакты сообщения регистрируются в сторе панели, пока пузырь смонтирован (то есть пока его
 * чат открыт), — так «тот же identifier позже в чате» становится новой версией.
 */
export const AssistantContent = memo(function AssistantContent({
  messageId,
  createdAt,
  content,
  streaming,
}: AssistantContentProps) {
  // STREAM_THROTTLE: see settledPrefix / STREAM_PARSE_INTERVAL_MS. The settled prefix is what the
  // parser sees; everything after it is appended as plain text at the bottom, so the answer still
  // grows every frame and only its formatting catches up on the next tick. When the stream ends the
  // whole text is parsed once.
  // SMOOTH_STREAM: сетевые чанки сглаживаются в равномерный поток на rAF.
  const smoothContent = useSmoothStream(content, streaming);
  const [parsedContent, setParsedContent] = useState(() => settledPrefix(content));
  const liveContentRef = useRef(smoothContent);
  liveContentRef.current = smoothContent;

  useEffect(() => {
    if (!streaming) {
      setParsedContent(liveContentRef.current);
      return;
    }
    const id = window.setInterval(() => {
      const next = settledPrefix(liveContentRef.current);
      setParsedContent((prev) => (next === prev ? prev : next));
    }, STREAM_PARSE_INTERVAL_MS);
    return () => window.clearInterval(id);
  }, [streaming]);

  // Content can shrink or be replaced (regenerate, edit, resume), and then the settled prefix is no
  // longer a prefix at all — showing a stale tail after it would duplicate text.
  const settled = smoothContent.startsWith(parsedContent) ? parsedContent : smoothContent;
  const rawTail = settled === smoothContent ? '' : smoothContent.slice(settled.length);
  const tail = streaming ? sanitizeStreamingTail(rawTail) : rawTail;

  // STREAM_SANITIZER: во время стриминга закрываем незавершённые **, `, ~~ до рендера.
  const sanitizedSettled = streaming ? closeStreamingInline(settled) : settled;
  const segments = useMemo(() => splitArtifacts(sanitizedSettled, streaming), [sanitizedSettled, streaming]);
  // An artifact whose opening tag is still arriving has no reliable identifier yet, so it is not
  // registered (it is always the last one, which keeps the `messageId:index` keys stable).
  const artifacts = useMemo(
    () => segments.flatMap((s) => (s.kind === 'artifact' && !s.artifact.pendingTag ? [s.artifact] : [])),
    [segments],
  );

  useEffect(() => {
    registerMessageArtifacts(messageId, createdAt, artifacts);
  }, [messageId, createdAt, artifacts]);

  useEffect(() => () => unregisterMessage(messageId), [messageId]);

  // Like Claude: an artifact that starts arriving in the live answer opens the panel by itself
  // (desktop only — on a phone the panel is full screen and would hide the chat). A panel the user
  // opened on something else is left alone.
  // Only text that actually grows on screen counts as "arriving": a message restored in the
  // streaming state after a reload must not pop the panel open by itself.
  const initialContent = useRef(content);
  useEffect(() => {
    if (!streaming || content === initialContent.current || !isWideScreen()) return;
    const live = artifacts.findIndex((a) => !a.complete);
    if (live < 0) return;
    const key = `${messageId}:${live}`;
    if (autoOpened.has(key)) return;
    autoOpened.add(key);
    const open = getArtifactState().open;
    if (open && !open.auto) return;
    openArtifact(artifacts[live].identifier, null, { auto: true });
  }, [streaming, content, artifacts, messageId]);

  if (segments.length === 1 && segments[0].kind === 'markdown' && !tail) {
    return <Markdown text={segments[0].text} />;
  }

  return (
    <>
      {segments.map((segment, i) =>
        segment.kind === 'markdown' ? (
          <Markdown key={`md-${i}`} text={segment.text} />
        ) : (
          <ArtifactCard
            key={`artifact-${segment.index}`}
            artifact={segment.artifact}
            entryKey={`${messageId}:${segment.index}`}
          />
        ),
      )}
      {/* The not-yet-parsed tail of a live answer: plain text, growing every frame. */}
      {tail && (
        <span className="stream-tail">
          {streaming ? <StreamingTail text={plainStreamTail(tail)} /> : tail}
        </span>
      )}
    </>
  );
});
