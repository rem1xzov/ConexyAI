import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { ChatMessage } from '../types/chat';
import type { TodoItem } from '../types/signalr';
import { CopyIcon, CloseIcon } from './Icons';

/**
 * CHAT_EXTRAS: источники ответа, follow-up подсказки и артефакт с кодом.
 *
 * Источники: Smart Search присылает URL текстом — вытаскиваем их из готового ответа и показываем
 * карточками с доменом (как Perplexity), не меняя формат самого сообщения.
 * Follow-up: короткие кнопки-подсказки под ответом; клик кладёт текст в композер (InputBar слушает
 * событие `conexy:insert-prompt`) — пользователю не нужно печатать.
 * Артефакт: длинный код из ответа открывается в модалке с копированием/скачиванием (MVP Canvas).
 */
export function MessageExtras({ message }: { message: ChatMessage }) {
  const { t } = useTranslation();
  const [artifactOpen, setArtifactOpen] = useState(false);
  const content = message.content ?? '';

  const urls = useMemo(() => extractUrls(content), [content]);
  const codeBlocks = useMemo(() => extractCodeBlocks(content), [content]);
  const suggestions = useMemo(() => buildSuggestions(content, urls.length > 0), [content, urls.length]);

  if (message.role !== 'assistant' || message.status !== 'complete') return null;
  if (!urls.length && !suggestions.length && !codeBlocks.length) return null;

  return (
    <div className="msg-extras">
      {urls.length > 0 && (
        <div className="msg-sources">
          <div className="msg-sources__title">{t('extras.sources', { count: urls.length })}</div>
          <div className="msg-sources__list">
            {urls.map((u, i) => (
              <a key={u.href} className="msg-source" href={u.href} target="_blank" rel="noreferrer noopener">
                <span className="msg-source__num">{i + 1}</span>
                <span className="msg-source__meta">
                  <span className="msg-source__host">{u.host}</span>
                  <span className="msg-source__path">{u.path}</span>
                </span>
              </a>
            ))}
          </div>
        </div>
      )}

      {codeBlocks.length > 0 && (
        <button className="msg-artifact-btn" type="button" onClick={() => setArtifactOpen(true)}>
          <span aria-hidden="true">▣</span> {t('extras.openArtifact', { count: codeBlocks.length })}
        </button>
      )}

      {suggestions.length > 0 && (
        <div className="msg-followups">
          {suggestions.map((s) => (
            <button
              key={s}
              type="button"
              className="msg-followup"
              onClick={() => {
                window.dispatchEvent(new CustomEvent('conexy:insert-prompt', { detail: { text: s } }));
              }}
            >
              {s}
            </button>
          ))}
        </div>
      )}

      {artifactOpen && (
        <ArtifactModal blocks={codeBlocks} onClose={() => setArtifactOpen(false)} />
      )}
    </div>
  );
}

function ArtifactModal({
  blocks,
  onClose,
}: {
  blocks: { lang: string; code: string }[];
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const [active, setActive] = useState(0);
  const block = blocks[Math.min(active, blocks.length - 1)];
  const fileName = `artifact-${active + 1}.${extensionFor(block.lang)}`;

  function download() {
    const blob = new Blob([block.code], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
    URL.revokeObjectURL(url);
  }

  return (
    <div className="artifact-overlay" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="artifact-modal" role="dialog" aria-modal="true">
        <div className="artifact-modal__head">
          <div className="artifact-modal__title">{t('extras.artifactTitle')}</div>
          <button className="icon-btn" type="button" onClick={onClose} aria-label={t('common.close')}>
            <CloseIcon size={16} />
          </button>
        </div>
        {blocks.length > 1 && (
          <div className="artifact-modal__tabs">
            {blocks.map((_, i) => (
              <button
                key={i}
                type="button"
                className={`artifact-tab${i === active ? ' artifact-tab--active' : ''}`}
                onClick={() => setActive(i)}
              >
                #{i + 1}
              </button>
            ))}
          </div>
        )}
        <div className="artifact-modal__lang">{block.lang || 'text'}</div>
        <pre className="artifact-modal__code">{block.code}</pre>
        <div className="artifact-modal__actions">
          <button
            className="dialog-btn"
            type="button"
            onClick={() => void navigator.clipboard?.writeText(block.code)}
          >
            <CopyIcon size={14} /> {t('extras.copy')}
          </button>
          <button className="dialog-btn dialog-btn--primary" type="button" onClick={download}>
            {t('extras.download')}
          </button>
        </div>
      </div>
    </div>
  );
}

function extractUrls(text: string): { href: string; host: string; path: string }[] {
  const matches = text.match(/https?:\/\/[^\s<>"'`)\]]+/g) ?? [];
  const seen = new Set<string>();
  const out: { href: string; host: string; path: string }[] = [];
  for (const raw of matches) {
    const href = raw.replace(/[.,;:!?]+$/, '');
    if (seen.has(href)) continue;
    try {
      const url = new URL(href);
      if (!/^https?:$/.test(url.protocol)) continue;
      seen.add(href);
      out.push({
        href,
        host: url.hostname.replace(/^www\./, ''),
        path: `${url.pathname === '/' ? '' : url.pathname}`.slice(0, 60),
      });
    } catch {
      // Not a real URL — skip.
    }
  }
  return out.slice(0, 5);
}

function extractCodeBlocks(text: string): { lang: string; code: string }[] {
  const out: { lang: string; code: string }[] = [];
  const re = /```([a-zA-Z0-9_+-]*)\n([\s\S]*?)```/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    const code = m[2].trim();
    if (code.length >= 80) out.push({ lang: m[1] || 'text', code });
  }
  return out;
}

function buildSuggestions(text: string, hasSources: boolean): string[] {
  const words = text.trim().split(/\s+/).length;
  const hasCode = /```/.test(text);
  if (hasCode) return ['Объясни код построчно', 'Упрости пример', 'Добавь обработку ошибок'];
  if (hasSources) return ['Сделай выжимку', 'Какие источники надёжнее?', 'Сравни данные'];
  if (words > 120) return ['Сократи до 5 пунктов', 'Дай пример', 'Что дальше?'];
  return ['Продолжи', 'Дай пример', 'Объясни проще'];
}

function extensionFor(lang: string): string {
  const map: Record<string, string> = {
    ts: 'ts', tsx: 'tsx', js: 'js', jsx: 'jsx', py: 'py', cs: 'cs', csharp: 'cs',
    json: 'json', html: 'html', css: 'css', sql: 'sql', bash: 'sh', sh: 'sh', md: 'md', yaml: 'yml',
  };
  return map[lang.toLowerCase()] ?? 'txt';
}

/** PLAN: компактный прогресс плана задачи — что уже сделано и что в работе. */
export function WebVerifyCard({
  command,
  output,
  summary,
  failed,
}: {
  command?: string;
  output?: string;
  summary?: string;
  failed?: boolean;
}) {
  const { t } = useTranslation();
  const text = output || summary || '';
  const pass = !failed && /PASS/i.test(text);
  const steps = text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => /^\d+\./.test(line))
    .slice(0, 6);
  const jsErrors = text.match(/ошибки JS:\s*(.+)/i)?.[1]?.trim();
  const url = (command || '').match(/https?:\/\/\S+/)?.[0] ?? command;

  return (
    <div className={`verify-card${pass ? ' verify-card--pass' : ' verify-card--fail'}`}>
      <div className="verify-card__head">
        <span className="verify-card__badge">{pass ? 'PASS' : 'FAIL'}</span>
        <span className="verify-card__title">{t('extras.verifyTitle')}</span>
        {url && <span className="verify-card__url">{url}</span>}
      </div>
      {steps.length > 0 && (
        <ul className="verify-card__steps">
          {steps.map((step) => (
            <li key={step}>{step}</li>
          ))}
        </ul>
      )}
      {jsErrors && <div className="verify-card__errors">{t('extras.verifyJsErrors', { errors: jsErrors })}</div>}
    </div>
  );
}

/** PLAN: компактный прогресс плана задачи — что уже сделано и что в работе. */
export function PlanProgress({ todos }: { todos: TodoItem[] }) {
  const { t } = useTranslation();
  if (todos.length === 0) return null;
  const done = todos.filter((x) => x.status === 'completed').length;
  const active = todos.find((x) => x.status === 'in_progress');
  const percent = Math.round((done / todos.length) * 100);

  return (
    <div className="plan-card">
      <div className="plan-card__head">
        <span className="plan-card__title">{t('extras.planTitle')}</span>
        <span className="plan-card__count">
          {done}/{todos.length}
        </span>
      </div>
      <div className="plan-card__bar">
        <span style={{ width: `${percent}%` }} />
      </div>
      <div className="plan-card__current">
        {active ? active.content : done === todos.length ? t('extras.planDone') : t('extras.planPending')}
      </div>
    </div>
  );
}
