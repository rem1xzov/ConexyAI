import { memo, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { codeBlockPreviewType } from '../../utils/artifacts';
import { copyText } from '../../utils/clipboard';
import { openAdhocArtifact } from '../artifacts/store';
import { HighlightedCode } from './HighlightedCode';

interface CodeBlockProps {
  language: string;
  code: string;
  /** False while the closing fence has not arrived yet (streaming). */
  closed?: boolean;
}

/** Copy button with a short "Copied" confirmation; shared by code blocks and diagrams. */
export function CopyButton({ text, className }: { text: string; className?: string }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);
  const timer = useRef<number | undefined>(undefined);

  useEffect(() => () => window.clearTimeout(timer.current), []);

  async function copy() {
    if (!(await copyText(text))) return;
    setCopied(true);
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setCopied(false), 1500);
  }

  return (
    <button
      className={className ?? 'code-block__copy'}
      onClick={() => void copy()}
      type="button"
      aria-label={t('render.copyCode')}
    >
      {copied ? t('render.copied') : t('render.copy')}
    </button>
  );
}

/**
 * Fenced code block: language label, optional "Preview" (```html / ```svg open the artifact
 * panel), copy button and highlighted code.
 */
export const CodeBlock = memo(function CodeBlock({ language, code, closed = true }: CodeBlockProps) {
  const { t } = useTranslation();
  const previewType = closed ? codeBlockPreviewType(language, code) : null;

  return (
    <div className="code-block">
      <div className="code-block__header">
        <span className="code-block__lang">{language || t('render.code')}</span>
        <div className="code-block__actions">
          {previewType && (
            <button
              className="code-block__copy"
              type="button"
              onClick={() =>
                openAdhocArtifact({
                  type: previewType,
                  title: t(previewType === 'text/html' ? 'render.htmlPreviewTitle' : 'render.svgPreviewTitle'),
                  language,
                  content: code,
                })
              }
            >
              {t('render.preview')}
            </button>
          )}
          <CopyButton text={code} />
        </div>
      </div>
      <pre className="code-block__pre">
        <HighlightedCode code={code} language={language} />
      </pre>
    </div>
  );
});
