import { memo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { downloadWorkspaceRaw } from '../api/conexyApi';
import { triggerDownload } from '../utils/download';
import type { ProducedFile } from '../utils/producedFiles';
import { FileTypeIcon } from './FileTypeIcon';
import { DownloadIcon, FolderIcon } from './Icons';

/** Collapsed beyond this many cards (documents always stay visible). */
const VISIBLE_LIMIT = 6;

const KIND_KEYS: Record<string, string> = {
  docx: 'render.fileWord',
  doc: 'render.fileWord',
  odt: 'render.fileWord',
  rtf: 'render.fileWord',
  xlsx: 'render.fileExcel',
  xls: 'render.fileExcel',
  ods: 'render.fileExcel',
  pptx: 'render.filePowerPoint',
  ppt: 'render.filePowerPoint',
  odp: 'render.filePowerPoint',
  pdf: 'render.filePdf',
  csv: 'render.fileCsv',
  tsv: 'render.fileCsv',
  md: 'render.fileMarkdown',
  txt: 'render.fileText',
};

interface FileCardProps {
  file: ProducedFile;
  chatId?: string;
  onOpenFile?: (path: string) => void;
}

function FileCard({ file, chatId, onOpenFile }: FileCardProps) {
  const { t } = useTranslation();
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  const folder = file.path.includes('/') ? file.path.slice(0, file.path.lastIndexOf('/')) : '';
  const kind = KIND_KEYS[file.ext] ? t(KIND_KEYS[file.ext]) : file.ext ? file.ext.toUpperCase() : t('render.fileGeneric');
  const meta = [kind, file.created ? t('render.fileCreated') : t('render.fileEdited'), folder].filter(Boolean).join(' · ');

  async function download() {
    if (!chatId || busy) return;
    setBusy(true);
    setFailed(false);
    try {
      triggerDownload(await downloadWorkspaceRaw(chatId, file.path), file.name);
    } catch {
      setFailed(true);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className={`file-card ${file.document ? 'file-card--document' : ''}`}>
      <span className="file-card__icon" aria-hidden="true">
        <FileTypeIcon path={file.name} size={30} />
      </span>
      <span className="file-card__text">
        <span className="file-card__name" title={file.path}>
          {file.name}
        </span>
        <span className="file-card__meta">{failed ? <span className="file-card__error">{t('render.downloadFailed')}</span> : meta}</span>
      </span>
      <span className="file-card__actions">
        {onOpenFile && (
          <button
            type="button"
            className="file-card__btn"
            onClick={() => onOpenFile(file.path)}
            title={t('render.openInWorkspace')}
            aria-label={`${t('render.openInWorkspace')}: ${file.name}`}
          >
            <FolderIcon size={15} />
          </button>
        )}
        {chatId && (
          <button
            type="button"
            className="file-card__btn file-card__btn--primary"
            onClick={() => void download()}
            disabled={busy}
            title={t('render.download')}
            aria-label={`${t('render.download')}: ${file.name}`}
          >
            <DownloadIcon size={15} />
            <span className="file-card__btn-label">{busy ? '…' : t('render.download')}</span>
          </button>
        )}
      </span>
    </div>
  );
}

interface ProducedFilesProps {
  files: ProducedFile[];
  /** Chat (= workspace) id; without it only "open in workspace" is offered. */
  chatId?: string;
  onOpenFile?: (path: string) => void;
}

/**
 * FILE_CARDS: добавлено 2026-09-24 — аккуратные карточки файлов, созданных агентом за ход, прямо
 * под ответом: документы (.docx/.xlsx/.pptx/.pdf/.csv/.md) первыми, с кнопкой «Скачать».
 */
export const ProducedFiles = memo(function ProducedFiles({ files, chatId, onOpenFile }: ProducedFilesProps) {
  const { t } = useTranslation();
  const [expanded, setExpanded] = useState(false);

  if (files.length === 0 || (!chatId && !onOpenFile)) return null;

  const ordered = [...files.filter((f) => f.document), ...files.filter((f) => !f.document)];
  const documentCount = files.filter((f) => f.document).length;
  const limit = Math.max(VISIBLE_LIMIT, documentCount);
  const visible = expanded ? ordered : ordered.slice(0, limit);
  const hidden = ordered.length - visible.length;

  return (
    <section className="produced-files" aria-label={t('render.filesTitle')}>
      <div className="produced-files__title">
        {t('render.filesTitle')} <span className="produced-files__count">{files.length}</span>
      </div>
      <div className="produced-files__grid">
        {visible.map((file) => (
          <FileCard key={file.path} file={file} chatId={chatId} onOpenFile={onOpenFile} />
        ))}
      </div>
      {(hidden > 0 || expanded) && ordered.length > limit && (
        <button type="button" className="produced-files__more" onClick={() => setExpanded((v) => !v)}>
          {expanded ? t('render.filesShowLess') : t('render.filesShowMore', { count: hidden })}
        </button>
      )}
    </section>
  );
});
