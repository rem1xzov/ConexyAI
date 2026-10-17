import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { deleteSkill, listSkills, setSkillEnabled, uploadSkill, type AgentSkill } from '../api/conexyApi';
import { CloseIcon, TrashIcon, UploadIcon } from './Icons';

interface SkillsDialogProps {
  onClose: () => void;
}

function errorText(e: unknown, fallback: string): string {
  const message = (e as { response?: { data?: { message?: string } } }).response?.data?.message;
  return typeof message === 'string' && message.length > 0 ? message : fallback;
}

// AGENT_SKILLS: добавлено 2026-10-18 — скиллы агента: файл SKILL.md (как в Claude) с именем, описанием
// «когда применять» и инструкциями. Агент видит список и сам загружает нужный скилл по описанию.
// Те же скиллы доступны в десктопе (/skill).
export function SkillsDialog({ onClose }: SkillsDialogProps) {
  const { t } = useTranslation();
  const [skills, setSkills] = useState<AgentSkill[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [dragging, setDragging] = useState(false);
  const fileRef = useRef<HTMLInputElement>(null);

  async function reload() {
    try {
      setSkills(await listSkills());
    } catch (e) {
      setError(errorText(e, t('skills.loadError')));
      setSkills([]);
    }
  }

  useEffect(() => {
    void reload();
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function add(files: FileList | File[]) {
    setError(null);
    setNotice(null);
    setBusy(true);
    const added: string[] = [];
    try {
      for (const file of Array.from(files)) {
        if (!/\.(md|markdown|txt)$/i.test(file.name)) {
          setError(t('skills.wrongType', { name: file.name }));
          continue;
        }
        try {
          const skill = await uploadSkill(file);
          added.push(skill.name);
        } catch (e) {
          setError(errorText(e, t('skills.uploadError')));
        }
      }
      if (added.length) setNotice(t('skills.added', { names: added.join(', ') }));
      await reload();
    } finally {
      setBusy(false);
    }
  }

  async function toggle(skill: AgentSkill) {
    setSkills((list) => list?.map((s) => (s.id === skill.id ? { ...s, enabled: !s.enabled } : s)) ?? list);
    try {
      await setSkillEnabled(skill.id, !skill.enabled);
    } catch (e) {
      setError(errorText(e, t('skills.actionError')));
      void reload();
    }
  }

  async function remove(skill: AgentSkill) {
    if (!window.confirm(t('skills.deleteConfirm', { name: skill.name }))) return;
    try {
      await deleteSkill(skill.id);
      setSkills((list) => list?.filter((s) => s.id !== skill.id) ?? list);
    } catch (e) {
      setError(errorText(e, t('skills.actionError')));
    }
  }

  return (
    <div className="dialog-overlay" onMouseDown={onClose}>
      <div
        className={`dialog-card skills-dialog ${dragging ? 'skills-dialog--drag' : ''}`}
        role="dialog"
        aria-modal="true"
        aria-label={t('skills.title')}
        onMouseDown={(e) => e.stopPropagation()}
        onDragOver={(e) => {
          e.preventDefault();
          setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          e.preventDefault();
          setDragging(false);
          if (e.dataTransfer.files.length) void add(e.dataTransfer.files);
        }}
      >
        <div className="skills-dialog__head">
          <div className="dialog-title">{t('skills.title')}</div>
          <button className="icon-btn" type="button" onClick={onClose} aria-label={t('common.close')}>
            <CloseIcon size={18} />
          </button>
        </div>
        <p className="muted skills-dialog__hint">{t('skills.hint')}</p>

        <button className="skills-dialog__add" type="button" disabled={busy} onClick={() => fileRef.current?.click()}>
          <UploadIcon size={17} />
          <span>{busy ? t('skills.uploading') : t('skills.add')}</span>
        </button>
        <input
          ref={fileRef}
          type="file"
          accept=".md,.markdown,.txt,text/markdown,text/plain"
          multiple
          hidden
          onChange={(e) => {
            if (e.target.files?.length) void add(e.target.files);
            e.target.value = '';
          }}
        />

        {error && <div className="skills-dialog__error">{error}</div>}
        {notice && <div className="skills-dialog__notice">{notice}</div>}

        <div className="skills-dialog__list">
          {skills === null ? (
            <p className="muted">{t('common.loading')}</p>
          ) : skills.length === 0 ? (
            <div className="skills-dialog__empty muted">
              <p>{t('skills.empty')}</p>
              <pre>{'---\nname: code-review\ndescription: ' + t('skills.exampleDescription') + '\n---\n' + t('skills.exampleBody')}</pre>
            </div>
          ) : (
            skills.map((s) => (
              <div key={s.id} className={`skills-item ${s.enabled ? '' : 'skills-item--off'}`}>
                <label className="skills-item__switch" title={s.enabled ? t('skills.disable') : t('skills.enable')}>
                  <input type="checkbox" checked={s.enabled} onChange={() => void toggle(s)} />
                  <span />
                </label>
                <div className="skills-item__body">
                  <div className="skills-item__name">{s.name}</div>
                  <div className="skills-item__description">{s.description}</div>
                </div>
                <button className="icon-btn" type="button" onClick={() => void remove(s)} aria-label={t('skills.delete')}>
                  <TrashIcon size={16} />
                </button>
              </div>
            ))
          )}
        </div>
      </div>
    </div>
  );
}
