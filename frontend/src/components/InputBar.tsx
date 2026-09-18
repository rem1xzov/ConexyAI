import { useEffect, useRef, useState } from 'react';
import type { ConexyModel, ReasoningEffort, TaskAttachment } from '../types/api';
import { fileToAttachment, isAllowedMime, pastedImageFile } from '../utils/attachments';
import { ModelPicker } from './ModelPicker';
import { VoiceWaveIcon, MicIcon, PlusIcon, SendIcon, StopIcon, UploadIcon, PhotoIcon, CameraIcon, CodeIcon, CloseIcon } from './Icons';

const MAX_ATTACHMENTS = 10;

interface InputBarProps {
  model: ConexyModel;
  onModelChange: (model: ConexyModel) => void;
  mode: 'chat' | 'code';
  thinking: boolean;
  onThinkingChange: (value: boolean) => void;
  reasoningEffort: ReasoningEffort;
  onReasoningEffortChange: (value: ReasoningEffort) => void;
  smartSearch: boolean;
  onSmartSearchChange: (value: boolean) => void;
  locked?: boolean;
  disabled?: boolean;
  isGenerating: boolean;
  onStop: () => void;
  // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
  // onOpenLive: () => void;
  onSend: (prompt: string, attachments: TaskAttachment[]) => void;
}

export function InputBar({
  model,
  onModelChange,
  mode,
  thinking,
  onThinkingChange,
  reasoningEffort,
  onReasoningEffortChange,
  smartSearch,
  onSmartSearchChange,
  locked,
  disabled,
  isGenerating,
  onStop,
  // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
  // onOpenLive,
  onSend,
}: InputBarProps) {
  const [value, setValue] = useState('');
  const [attachments, setAttachments] = useState<TaskAttachment[]>([]);
  const [isRecording, setIsRecording] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [limitHint, setLimitHint] = useState(false);

  const fileRef = useRef<HTMLInputElement>(null);
  const photoRef = useRef<HTMLInputElement>(null);
  const cameraRef = useRef<HTMLInputElement>(null);
  const codeRef = useRef<HTMLInputElement>(null);
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  const recognitionRef = useRef<any>(null);

  useEffect(() => {
    function onClickOutside(e: MouseEvent) {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setMenuOpen(false);
      }
    }
    document.addEventListener('mousedown', onClickOutside);
    return () => document.removeEventListener('mousedown', onClickOutside);
  }, []);

  // Stop any in-progress recognition if the component unmounts.
  useEffect(
    () => () => {
      recognitionRef.current?.stop();
    },
    [],
  );

  function resizeTextarea() {
    if (textareaRef.current) {
      textareaRef.current.style.height = 'auto';
      textareaRef.current.style.height = `${textareaRef.current.scrollHeight}px`;
    }
  }

  function submit() {
    const prompt = value.trim();
    if (!prompt || disabled) return;
    onSend(prompt, attachments);
    setValue('');
    setAttachments([]);
    if (textareaRef.current) textareaRef.current.style.height = 'auto';
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      submit();
    }
  }

  // Ctrl+V pastes an image from the clipboard as an attachment instead of inserting
  // a data URI / text into the textarea. Text paste falls through to the default.
  function handlePaste(e: React.ClipboardEvent<HTMLTextAreaElement>) {
    const items = e.clipboardData?.items;
    if (!items) return;

    for (const item of items) {
      if (item.kind === 'file' && item.type.startsWith('image/')) {
        e.preventDefault();
        const file = item.getAsFile();
        if (file) {
          void addFiles([pastedImageFile(file)], true);
        }
      }
    }
  }

  async function addFiles(files: FileList | File[], skipMimeCheck = false) {
    const next = [...attachments];
    for (const file of Array.from(files)) {
      if (!skipMimeCheck && !isAllowedMime(file.type)) continue;
      if (next.length >= MAX_ATTACHMENTS) {
        setLimitHint(true);
        window.setTimeout(() => setLimitHint(false), 2500);
        break;
      }
      next.push(await fileToAttachment(file));
    }
    setAttachments(next);
    setMenuOpen(false);
  }

  function handleMicClick() {
    const SpeechRecognition = (window as any).SpeechRecognition || (window as any).webkitSpeechRecognition;
    if (!SpeechRecognition) {
      alert('Ваш браузер не поддерживает голосовой ввод. Используйте Chrome или Яндекс Браузер.');
      return;
    }

    if (isRecording) {
      recognitionRef.current?.stop();
      setIsRecording(false);
      return;
    }

    const recognition = new SpeechRecognition();
    recognition.lang = 'ru-RU';
    recognition.continuous = false;
    recognition.interimResults = true;

    recognition.onstart = () => setIsRecording(true);

    recognition.onresult = (event: any) => {
      const transcript = Array.from(event.results)
        .map((result: any) => result[0].transcript)
        .join('');
      setValue((prev) => (prev ? `${prev} ` : '') + transcript);
      resizeTextarea();
    };

    recognition.onerror = (event: any) => {
      console.error('Speech recognition error', event.error);
      setIsRecording(false);
    };

    recognition.onend = () => setIsRecording(false);

    recognitionRef.current = recognition;
    recognition.start();
  }

  // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
  // Live-кнопка скрыта: кнопка отправки больше не переключается в live-режим.
  const hasInputText = value.trim().length > 0;
  // const isFlashModel = model === 'ConexyV1-flash';
  // const showVoiceButton = isFlashModel && !hasInputText;

  return (
    <div className="inputbar-wrap">
      {attachments.length > 0 && (
        <div className="inputbar-attachments">
          {attachments.map((a, i) => (
            <div key={`${a.fileName}-${i}`} className="relative shrink-0 w-[120px]">
              <div className="h-[80px] w-full rounded-lg border border-zinc-700 bg-zinc-900 overflow-hidden">
                {a.contentType.startsWith('image/') ? (
                  <img src={`data:${a.contentType};base64,${a.contentBase64}`} alt={a.fileName} className="w-full h-full object-cover" />
                ) : (
                  <div className="w-full h-full flex items-center justify-center text-2xl">📄</div>
                )}
              </div>
              <div className="mt-1 truncate text-[11px] text-zinc-400" title={a.fileName}>{a.fileName}</div>
              <button
                className="absolute -top-1.5 -right-1.5 w-5 h-5 rounded-full bg-zinc-700 hover:bg-red-500 text-white text-xs flex items-center justify-center"
                onClick={() => setAttachments((prev) => prev.filter((_, j) => j !== i))}
                aria-label={`Remove ${a.fileName}`}
              >
                <CloseIcon size={10} />
              </button>
            </div>
          ))}
        </div>
      )}
      {limitHint && (
        <div className="inputbar-limit">Максимум 10 файлов за сообщение</div>
      )}

      <div className="inputbar">
        <div className="attach-wrap" ref={menuRef}>
          <button
            className="icon-btn attach-btn"
            onClick={() => setMenuOpen((o) => !o)}
            title="Add files"
            aria-label="Add files"
          >
            <PlusIcon size={20} />
          </button>

          {menuOpen && (
            <div className="attach-menu">
              <button className="attach-menu__item" onClick={() => fileRef.current?.click()}>
                <UploadIcon size={17} /> Upload files
              </button>
              <button className="attach-menu__item" onClick={() => photoRef.current?.click()}>
                <PhotoIcon size={17} /> Photos
              </button>
              <button className="attach-menu__item" onClick={() => cameraRef.current?.click()}>
                <CameraIcon size={17} /> Take photo
              </button>
              <button className="attach-menu__item" onClick={() => codeRef.current?.click()}>
                <CodeIcon size={17} /> Import code
              </button>
            </div>
          )}
        </div>

        <input
          ref={fileRef}
          type="file"
          multiple
          hidden
          onChange={(e) => {
            if (e.target.files?.length) void addFiles(e.target.files);
            e.target.value = '';
          }}
        />
        <input
          ref={photoRef}
          type="file"
          accept="image/*"
          multiple
          hidden
          onChange={(e) => {
            if (e.target.files?.length) void addFiles(e.target.files);
            e.target.value = '';
          }}
        />
        <input
          ref={cameraRef}
          type="file"
          accept="image/*"
          capture="environment"
          hidden
          onChange={(e) => {
            if (e.target.files?.length) void addFiles(e.target.files);
            e.target.value = '';
          }}
        />
        <input
          ref={codeRef}
          type="file"
          accept=".cs,.ts,.tsx,.js,.jsx,.json,.py,.html,.css,.sql"
          multiple
          hidden
          onChange={(e) => {
            if (e.target.files?.length) void addFiles(e.target.files, true);
            e.target.value = '';
          }}
        />

        <textarea
          ref={textareaRef}
          className="inputbar__textarea"
          rows={1}
          disabled={disabled}
          placeholder="Message ConexyAI…"
          value={value}
          onChange={(e) => {
            setValue(e.target.value);
            resizeTextarea();
          }}
          onKeyDown={handleKeyDown}
          onPaste={handlePaste}
        />

        <ModelPicker
          model={model}
          onModelChange={onModelChange}
          mode={mode}
          thinking={thinking}
          onThinkingChange={onThinkingChange}
          reasoningEffort={reasoningEffort}
          onReasoningEffortChange={onReasoningEffortChange}
          smartSearch={smartSearch}
          onSmartSearchChange={onSmartSearchChange}
          locked={locked}
        />

        <button
          className={`mic-btn ${isRecording ? 'mic-btn--active' : ''}`}
          onClick={handleMicClick}
          title={isRecording ? 'Остановить запись' : 'Голосовой ввод'}
          aria-label={isRecording ? 'Остановить запись' : 'Голосовой ввод'}
          disabled={disabled}
        >
          <MicIcon size={19} />
        </button>

        {isGenerating ? (
          <button
            className="send-btn send-btn--stop"
            onClick={onStop}
            title="Остановить генерацию"
            aria-label="Остановить генерацию"
          >
            <StopIcon size={18} />
          </button>
        ) : (
          <button
            type="submit"
            onClick={submit}
            title="Отправить"
            aria-label="Отправить"
            className="send-btn"
            disabled={disabled || !hasInputText}
          >
            <span className="send-btn__icon">
              <VoiceWaveIcon size={16} />
            </span>
            <span className="send-btn__icon send-btn__icon--on">
              <SendIcon size={16} />
            </span>
          </button>
        )}
      </div>
    </div>
  );
}
