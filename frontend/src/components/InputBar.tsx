import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { ConexyModel, ReasoningEffort, TaskAttachment } from '../types/api';
import { fileToAttachment, isAllowedMime, pastedImageFile } from '../utils/attachments';
import { useIsMobile } from '../hooks/useMediaQuery';
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
  const { t } = useTranslation();
  // The model picker lives in the chat header on mobile and inline here on desktop.
  const isMobile = useIsMobile();
  const [value, setValue] = useState('');
  const [attachments, setAttachments] = useState<TaskAttachment[]>([]);
  const [isRecording, setIsRecording] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [limitHint, setLimitHint] = useState(false);
  // Visible reason why voice input could not start (permission, no device, unsupported…).
  const [micError, setMicError] = useState<string | null>(null);

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
  // Consecutive recognizer sessions that ended without a single result. Guards against
  // an endless start/stop loop that would keep the button "active" while nothing records.
  const emptySessionsRef = useRef(0);
  const sessionHasResultRef = useRef(false);

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

  function micErrorText(kind: string): string {
    switch (kind) {
      case 'unsupported':
        return t('input.micUnsupported');
      case 'insecure':
        return t('input.micInsecure');
      case 'denied':
        return t('input.micDenied');
      case 'no-device':
        return t('input.micNoDevice');
      case 'busy':
        return t('input.micBusy');
      case 'network':
        return t('input.micNetwork');
      case 'no-speech':
        return t('input.micNoSpeech');
      default:
        return t('input.micFailed');
    }
  }

  // Maps a DOMException from getUserMedia (or a SpeechRecognition error name) to a message
  // the user can act on, and always logs the raw cause for diagnostics.
  function reportMicError(cause: unknown, fallbackKind = 'failed') {
    const name = String((cause as { name?: string })?.name ?? cause ?? '');
    const kind =
      name === 'NotAllowedError' || name === 'PermissionDeniedError' || name === 'SecurityError'
        ? 'denied'
        : name === 'NotFoundError' || name === 'DevicesNotFoundError'
          ? 'no-device'
          : name === 'NotReadableError' || name === 'TrackStartError' || name === 'AbortError'
            ? 'busy'
            : name === 'network'
              ? 'network'
              : name === 'no-speech'
                ? 'no-speech'
                : fallbackKind;
    console.error('[Voice] cannot start voice input:', name, cause);
    setMicError(micErrorText(kind));
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

  async function handleMicClick() {
    setMicError(null);

    const SpeechRecognition = (window as any).SpeechRecognition || (window as any).webkitSpeechRecognition;

    // One-shot diagnostic block: reproduce the click and send this line to debug.
    console.info('[Voice] mic click', {
      secureContext: window.isSecureContext,
      hasSpeechRecognition: Boolean(SpeechRecognition),
      hasWebkitSpeechRecognition: Boolean((window as any).webkitSpeechRecognition),
      hasGetUserMedia: Boolean(navigator.mediaDevices?.getUserMedia),
      currentlyListening: listeningRef.current,
      userAgent: navigator.userAgent,
    });

    if (listeningRef.current) {
      console.info('[Voice] stopping on user request');
      stopRecognition();
      return;
    }

    // The Web Speech API only works in a secure context.
    if (!window.isSecureContext) {
      reportMicError('insecure-context', 'insecure');
      return;
    }

    if (!SpeechRecognition) {
      reportMicError('no-speech-recognition-api', 'unsupported');
      return;
    }

    // Ask for the microphone up front. SpeechRecognition swallows a missing permission —
    // it just never fires onresult — so probing getUserMedia turns a silent no-op into a
    // concrete, user-visible error (NotAllowedError / NotFoundError / NotReadableError).
    if (navigator.mediaDevices?.getUserMedia) {
      try {
        const probe = await navigator.mediaDevices.getUserMedia({ audio: true });
        probe.getTracks().forEach((track) => track.stop());
        console.info('[Voice] microphone permission granted');
      } catch (err) {
        reportMicError(err);
        return;
      }
    } else {
      console.warn('[Voice] navigator.mediaDevices.getUserMedia unavailable; skipping permission probe');
    }

    baseTextRef.current = value.trim();
    finalTextRef.current = '';
    interimRef.current = '';
    liveTextRef.current = '';
    emptySessionsRef.current = 0;
    sessionHasResultRef.current = false;
    listeningRef.current = true;

    const recognition = new SpeechRecognition();
    recognition.lang = 'ru-RU';
    // Must stay `true`: with `false` the browser ends the session after the first
    // pause in speech (or a short internal timeout), which is what used to stop the
    // recording after roughly a second.
    recognition.continuous = true;
    recognition.interimResults = true;
    recognition.maxAlternatives = 1;

    recognition.onstart = () => {
      console.info('[Voice] recognition started');
      setIsRecording(true);
    };

    recognition.onaudiostart = () => console.info('[Voice] audio capture started');
    recognition.onspeechstart = () => console.info('[Voice] speech detected');
    recognition.onspeechend = () => console.info('[Voice] speech ended');

    recognition.onresult = (event: any) => {
      sessionHasResultRef.current = true;
      emptySessionsRef.current = 0;
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
      console.info('[Voice] result', { final, interim });
    };

    recognition.onerror = (event: any) => {
      console.error('[Voice] recognition error:', event.error, event);
      switch (event.error) {
        case 'not-allowed':
        case 'service-not-allowed':
          stopRecognition();
          reportMicError({ name: 'NotAllowedError' });
          break;
        case 'audio-capture':
          stopRecognition();
          reportMicError({ name: 'NotFoundError' });
          break;
        case 'network':
          stopRecognition();
          reportMicError({ name: 'network' });
          break;
        case 'aborted':
          // Fired by our own stop(); nothing to report.
          break;
        case 'no-speech':
          // Transient; the restart in onend keeps listening.
          break;
        default:
          stopRecognition();
          reportMicError(event.error);
          break;
      }
    };

    // The browser may still end a continuous session on its own (long silence, network
    // hiccup). Restart it so listening lasts until the user taps the mic again.
    recognition.onend = () => {
      console.info('[Voice] recognition ended', {
        listening: listeningRef.current,
        hadResult: sessionHasResultRef.current,
      });
      if (!listeningRef.current) return;

      commitInterim();

      // A session that produced nothing at all means the recognizer cannot actually work
      // here (blocked service, dropped packets). Bail out instead of looping forever.
      if (!sessionHasResultRef.current) {
        emptySessionsRef.current += 1;
        if (emptySessionsRef.current > 5) {
          stopRecognition();
          setMicError(micErrorText('failed'));
          return;
        }
      }
      sessionHasResultRef.current = false;

      restartTimerRef.current = window.setTimeout(() => {
        restartTimerRef.current = null;
        if (!listeningRef.current) return;
        try {
          recognition.start();
        } catch (err) {
          console.error('[Voice] restart failed:', err);
          stopRecognition();
          reportMicError(err);
        }
      }, 250);
    };

    recognitionRef.current = recognition;
    try {
      recognition.start();
    } catch (err) {
      recognitionRef.current = null;
      listeningRef.current = false;
      setIsRecording(false);
      reportMicError(err);
    }
  }

  function handleMicErrorDismiss() {
    setMicError(null);
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
      {micError && (
        <div className="inputbar-error" role="alert">
          <span className="inputbar-error__text">{micError}</span>
          <button
            className="inputbar-error__close"
            onClick={handleMicErrorDismiss}
            aria-label={t('common.close')}
            type="button"
          >
            <CloseIcon size={12} />
          </button>
        </div>
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

        {/* Desktop only: on mobile the model picker lives in the chat header. */}
        {!isMobile && (
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
        )}

        <button
          className={`mic-btn ${isRecording ? 'mic-btn--active' : ''}`}
          onClick={handleMicClick}
          type="button"
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
