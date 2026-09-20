import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { TaskAttachment } from '../types/api';
import { fileToAttachment, isAllowedMime, pastedImageFile } from '../utils/attachments';
import { VoiceWaveIcon, MicIcon, PlusIcon, SendIcon, StopIcon, UploadIcon, PhotoIcon, CameraIcon, CodeIcon, CloseIcon } from './Icons';

const MAX_ATTACHMENTS = 10;

interface InputBarProps {
  disabled?: boolean;
  isGenerating: boolean;
  onStop: () => void;
  // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
  // onOpenLive: () => void;
  onSend: (prompt: string, attachments: TaskAttachment[]) => void;
}

export function InputBar({
  disabled,
  isGenerating,
  onStop,
  // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
  // onOpenLive,
  onSend,
}: InputBarProps) {
  const { t } = useTranslation();
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
  // True while the user wants the mic on. Drives the auto-restart in `onend` so a
  // browser-side timeout never silently ends the session before the user taps stop.
  const listeningRef = useRef(false);
  // Text that was already in the textarea when recording started.
  const baseTextRef = useRef('');
  // Everything finalized by the recognizer during this session (survives restarts).
  const finalTextRef = useRef('');
  // Latest not-yet-finalized tail from the current recognizer session.
  const interimRef = useRef('');
  // Finalized text + the pending interim tail, so stopping never loses the last phrase.
  const liveTextRef = useRef('');
  const restartTimerRef = useRef<number | null>(null);

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
      listeningRef.current = false;
      if (restartTimerRef.current !== null) {
        window.clearTimeout(restartTimerRef.current);
        restartTimerRef.current = null;
      }
      const recognition = recognitionRef.current;
      recognitionRef.current = null;
      if (recognition) {
        recognition.onend = null;
        recognition.onresult = null;
        recognition.onerror = null;
        try {
          recognition.stop();
        } catch {
          // already stopped
        }
      }
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

  function applyTranscript() {
    const spoken = liveTextRef.current.trim();
    const base = baseTextRef.current;
    setValue(base ? (spoken ? `${base} ${spoken}` : base) : spoken);
    resizeTextarea();
  }

  // Speech segments carry inconsistent whitespace, so join them with exactly one space.
  function joinTranscript(left: string, right: string): string {
    const a = left.trim();
    const b = right.trim();
    if (!a) return b;
    if (!b) return a;
    return `${a} ${b}`;
  }

  // A recognizer session is dropped on restart, and its pending interim result with it.
  // Fold that tail into the finalized text so nothing the user said disappears.
  function commitInterim() {
    const interim = interimRef.current.trim();
    interimRef.current = '';
    if (!interim) return;
    finalTextRef.current = joinTranscript(finalTextRef.current, interim);
    liveTextRef.current = finalTextRef.current;
  }

  function stopRecognition() {
    listeningRef.current = false;
    setIsRecording(false);
    if (restartTimerRef.current !== null) {
      window.clearTimeout(restartTimerRef.current);
      restartTimerRef.current = null;
    }
    const recognition = recognitionRef.current;
    recognitionRef.current = null;
    if (recognition) {
      // Detach first: onend would otherwise try to restart the session we're closing.
      recognition.onend = null;
      try {
        recognition.stop();
      } catch {
        // already stopped
      }
    }
    applyTranscript();
  }

  function handleMicClick() {
    const SpeechRecognition = (window as any).SpeechRecognition || (window as any).webkitSpeechRecognition;
    if (!SpeechRecognition) {
      alert(t('input.micUnsupported'));
      return;
    }

    if (listeningRef.current) {
      stopRecognition();
      return;
    }

    baseTextRef.current = value.trim();
    finalTextRef.current = '';
    interimRef.current = '';
    liveTextRef.current = '';
    listeningRef.current = true;

    const recognition = new SpeechRecognition();
    recognition.lang = 'ru-RU';
    // Must stay `true`: with `false` the browser ends the session after the first
    // pause in speech (or a short internal timeout), which is what used to stop the
    // recording after roughly a second.
    recognition.continuous = true;
    recognition.interimResults = true;

    recognition.onstart = () => setIsRecording(true);

    recognition.onresult = (event: any) => {
      let interim = '';
      let final = '';
      // Start from resultIndex: earlier results were already committed to finalTextRef.
      for (let i = event.resultIndex; i < event.results.length; i++) {
        const result = event.results[i];
        if (result.isFinal) final += result[0].transcript;
        else interim += result[0].transcript;
      }
      if (final) finalTextRef.current = joinTranscript(finalTextRef.current, final);
      interimRef.current = interim;
      liveTextRef.current = joinTranscript(finalTextRef.current, interim);
      applyTranscript();
    };

    recognition.onerror = (event: any) => {
      console.error('Speech recognition error', event.error);
      // Permission / hardware failures are terminal; no-speech or network hiccups are not.
      if (
        event.error === 'not-allowed' ||
        event.error === 'service-not-allowed' ||
        event.error === 'audio-capture'
      ) {
        stopRecognition();
      }
    };

    // The browser may still end a continuous session on its own (long silence, network
    // hiccup). Restart it so listening lasts until the user taps the mic again.
    recognition.onend = () => {
      if (!listeningRef.current) return;
      commitInterim();
      restartTimerRef.current = window.setTimeout(() => {
        restartTimerRef.current = null;
        if (!listeningRef.current) return;
        try {
          recognition.start();
        } catch (err) {
          console.error('Speech recognition restart failed', err);
          stopRecognition();
        }
      }, 250);
    };

    recognitionRef.current = recognition;
    try {
      recognition.start();
    } catch (err) {
      console.error('Speech recognition start failed', err);
      listeningRef.current = false;
      setIsRecording(false);
    }
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
              <div className="h-[80px] w-full rounded-lg chat-surface overflow-hidden">
                {a.contentType.startsWith('image/') ? (
                  <img src={`data:${a.contentType};base64,${a.contentBase64}`} alt={a.fileName} className="w-full h-full object-cover" />
                ) : (
                  <div className="w-full h-full flex items-center justify-center text-2xl">📄</div>
                )}
              </div>
              <div className="mt-1 truncate text-[11px] chat-muted" title={a.fileName}>{a.fileName}</div>
              <button
                className="absolute -top-1.5 -right-1.5 w-5 h-5 rounded-full chat-btn-danger text-xs flex items-center justify-center"
                onClick={() => setAttachments((prev) => prev.filter((_, j) => j !== i))}
                aria-label={t('input.removeFile', { name: a.fileName })}
              >
                <CloseIcon size={10} />
              </button>
            </div>
          ))}
        </div>
      )}
      {limitHint && (
        <div className="inputbar-limit">{t('input.maxFiles')}</div>
      )}

      <div className="inputbar">
        <div className="attach-wrap" ref={menuRef}>
          <button
            className="icon-btn attach-btn"
            onClick={() => setMenuOpen((o) => !o)}
            title={t('input.addFiles')}
            aria-label={t('input.addFiles')}
          >
            <PlusIcon size={20} />
          </button>

          {menuOpen && (
            <div className="attach-menu">
              <button className="attach-menu__item" onClick={() => fileRef.current?.click()}>
                <UploadIcon size={17} /> {t('input.uploadFiles')}
              </button>
              <button className="attach-menu__item" onClick={() => photoRef.current?.click()}>
                <PhotoIcon size={17} /> {t('input.photos')}
              </button>
              <button className="attach-menu__item" onClick={() => cameraRef.current?.click()}>
                <CameraIcon size={17} /> {t('input.takePhoto')}
              </button>
              <button className="attach-menu__item" onClick={() => codeRef.current?.click()}>
                <CodeIcon size={17} /> {t('input.importCode')}
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
          placeholder={t('input.placeholder')}
          value={value}
          onChange={(e) => {
            setValue(e.target.value);
            resizeTextarea();
          }}
          onKeyDown={handleKeyDown}
          onPaste={handlePaste}
        />

        <button
          className={`mic-btn ${isRecording ? 'mic-btn--active' : ''}`}
          onClick={handleMicClick}
          title={isRecording ? t('input.stopRecording') : t('input.voiceInput')}
          aria-label={isRecording ? t('input.stopRecording') : t('input.voiceInput')}
          disabled={disabled}
        >
          <MicIcon size={19} />
        </button>

        {isGenerating ? (
          <button
            className="send-btn send-btn--stop"
            onClick={onStop}
            title={t('input.stopGeneration')}
            aria-label={t('input.stopGeneration')}
          >
            <StopIcon size={18} />
          </button>
        ) : (
          <button
            type="submit"
            onClick={submit}
            title={t('common.send')}
            aria-label={t('common.send')}
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
