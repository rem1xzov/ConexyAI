import { memo, useMemo } from 'react';
import { tokenize } from '../../utils/highlight';

interface HighlightedCodeProps {
  code: string;
  language?: string;
  className?: string;
}

/**
 * RENDER_HL: добавлено 2026-09-24 — подсвеченный код как дерево React-элементов.
 * Токены выводятся текстом внутри <span>, React сам экранирует их, поэтому код модели не может
 * превратиться в разметку (в отличие от старого highlight(), который возвращал HTML-строку).
 */
export const HighlightedCode = memo(function HighlightedCode({ code, language, className }: HighlightedCodeProps) {
  const tokens = useMemo(() => tokenize(code, language), [code, language]);
  return (
    <code className={`hl ${className ?? ''}`.trim()}>
      {tokens.map((token, i) =>
        token.type === 'plain' ? (
          token.text
        ) : (
          <span key={i} className={`tok-${token.type}`}>
            {token.text}
          </span>
        ),
      )}
    </code>
  );
});
