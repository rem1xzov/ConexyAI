import { useEffect, useRef, useState } from 'react';
import { prefersReducedMotion } from '../motion/motionConfig';

/**
 * SMOOTH_STREAM
 * Сетевые чанки приходят рывками. Буфер отдаёт уже пришедший текст равномерными порциями
 * на requestAnimationFrame, чтобы Markdown-хвост рос плавно, а не «вспрыгивал» за один кадр.
 *
 * Читает target через ref, поэтому каждый новый токен не пересоздаёт rAF-цикл.
 * Вне активного стрима (история, завершённый ответ) сразу отдаёт полный текст — без анимации.
 */
export function useSmoothStream(target: string, streaming: boolean): string {
  const [display, setDisplay] = useState(target);
  const targetRef = useRef(target);
  const displayRef = useRef(display);
  const streamingRef = useRef(streaming);
  const wasStreamingRef = useRef(streaming);
  const frameRef = useRef<number | null>(null);

  targetRef.current = target;
  streamingRef.current = streaming;

  // История / ответ, открытый без стрима: показываем сразу.
  useEffect(() => {
    if (streaming) {
      wasStreamingRef.current = true;
      return;
    }
    if (!wasStreamingRef.current) {
      displayRef.current = targetRef.current;
      setDisplay(targetRef.current);
    }
    // Если стрим только что закончился — даём rAF-циклу догнать хвост, без рывка.
  }, [streaming]);

  useEffect(() => {
    if (prefersReducedMotion()) {
      displayRef.current = targetRef.current;
      setDisplay(targetRef.current);
      return;
    }

    const tick = () => {
      const target = targetRef.current;
      const current = displayRef.current;

      if (!streamingRef.current) {
        // Догоняем финальный хвост плавно; историю (без стрима) — мгновенно.
        if (wasStreamingRef.current && current.length < target.length) {
          const remaining = target.length - current.length;
          const step = Math.min(48, Math.max(8, Math.ceil(remaining / 4)));
          const next = target.slice(0, current.length + step);
          displayRef.current = next;
          setDisplay(next);
          frameRef.current = requestAnimationFrame(tick);
          return;
        }
        if (current !== target) {
          displayRef.current = target;
          setDisplay(target);
        }
        wasStreamingRef.current = false;
        frameRef.current = requestAnimationFrame(tick);
        return;
      }

      // Контент отредактировали/перегенерировали — не догоняем старый хвост.
      if (target.length < current.length || !target.startsWith(current)) {
        displayRef.current = target;
        setDisplay(target);
      } else if (current.length < target.length) {
        const remaining = target.length - current.length;
        // 2–10 символов за кадр: поток ровный, но успевает за сетью.
        const step = Math.min(10, Math.max(2, Math.ceil(remaining / 30)));
        const next = target.slice(0, current.length + step);
        displayRef.current = next;
        setDisplay(next);
      }
      frameRef.current = requestAnimationFrame(tick);
    };

    frameRef.current = requestAnimationFrame(tick);
    return () => {
      if (frameRef.current !== null) cancelAnimationFrame(frameRef.current);
      frameRef.current = null;
    };
  }, []);

  return display;
}
