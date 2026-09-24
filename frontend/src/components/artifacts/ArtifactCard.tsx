import { memo } from 'react';
import { useTranslation } from 'react-i18next';
import type { ArtifactKind, ParsedArtifact } from '../../utils/artifacts';
import { openArtifact, useArtifactSelector, versionsOf } from './store';
import { ArtifactIcon } from './ArtifactIcon';

const TYPE_LABEL_KEYS: Record<ArtifactKind, string> = {
  'text/html': 'render.typeHtml',
  'image/svg+xml': 'render.typeSvg',
  'text/mermaid': 'render.typeMermaid',
  'text/markdown': 'render.typeMarkdown',
  'application/code': 'render.typeCode',
};

export function artifactTypeLabel(t: (key: string) => string, type: ArtifactKind, language?: string): string {
  const label = t(TYPE_LABEL_KEYS[type]);
  return type === 'application/code' && language ? `${label} · ${language}` : label;
}

interface ArtifactCardProps {
  artifact: ParsedArtifact;
  /** Registry key of this occurrence (`${messageId}:${index}`), used to open exactly this version. */
  entryKey: string;
}

/**
 * ARTIFACTS: добавлено 2026-09-24 — карточка на месте тега <conexy_artifact> в сообщении.
 * Клик открывает правую панель именно на этой версии артефакта.
 */
export const ArtifactCard = memo(function ArtifactCard({ artifact, entryKey }: ArtifactCardProps) {
  const { t } = useTranslation();
  // "Version 2" only when the same identifier occurs more than once in this chat.
  const versionNumber = useArtifactSelector((s) => {
    const versions = versionsOf(s, artifact.identifier);
    if (versions.length < 2) return 0;
    return versions.findIndex((v) => v.key === entryKey) + 1;
  });
  const isOpen = useArtifactSelector((s) => {
    if (!s.open || s.open.identifier !== artifact.identifier) return false;
    if (s.open.key) return s.open.key === entryKey;
    const versions = versionsOf(s, artifact.identifier);
    return versions[versions.length - 1]?.key === entryKey;
  });

  const pending = !artifact.complete;
  const title = artifact.title || t('render.artifactUntitled');
  const meta = pending
    ? t('render.artifactCreating')
    : [artifactTypeLabel(t, artifact.type, artifact.language), versionNumber > 0 ? t('render.artifactVersion', { n: versionNumber }) : '']
        .filter(Boolean)
        .join(' · ');

  return (
    <div className={`artifact-card ${pending ? 'artifact-card--pending' : ''} ${isOpen ? 'artifact-card--active' : ''}`}>
      <button
        type="button"
        className="artifact-card__main"
        onClick={() => openArtifact(artifact.identifier, entryKey)}
        disabled={artifact.pendingTag}
        aria-label={`${title} — ${t('render.open')}`}
      >
        <span className="artifact-card__icon" aria-hidden="true">
          {pending ? <span className="artifact-card__spinner" /> : <ArtifactIcon type={artifact.type} size={20} />}
        </span>
        <span className="artifact-card__text">
          <span className="artifact-card__title">{title}</span>
          <span className="artifact-card__meta">{meta}</span>
        </span>
        <span className="artifact-card__open" aria-hidden="true">
          {t('render.open')}
        </span>
      </button>
    </div>
  );
});
