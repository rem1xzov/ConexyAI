import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { ConexyModel, ReasoningEffort, SendOutcome, TaskAttachment } from '../types/api';
import {
  MAX_TOTAL_ATTACHMENT_BYTES,
  attachmentBytes,
  estimateRequestBytes,
  fileToAttachment,
  formatMegabytes,
  isAllowedMime,
  MAX_REQUEST_BYTES,
  pastedImageFile,
} from '../utils/attachments';
import { useIsMobile, useMediaQuery } from '../hooks/useMediaQuery';
// VOICE_DUP_FIX: склейка распознанного текста по перекрытию (см. utils/voiceTranscript).
import { mergeTranscript } from '../utils/voiceTranscript';
import { ModelPicker } from './ModelPicker';
import { VoiceWaveIcon, MicIcon, PlusIcon, SendIcon, StopIcon, UploadIcon, PhotoIcon, CameraIcon, CodeIcon, CloseIcon } from './Icons';

const MAX_ATTACHMENTS = 10;

// ATTACHMENT_UPLOAD: добавлено 2026-09-27
/**
 * Вложение в композере. От <see cref="TaskAttachment"/> отличается только локальным состоянием:
 * `loading` — файл ещё читается (его base64 не готов), `previewUrl` — мгновенное превью из object URL.
 * В запрос уходят только поля TaskAttachment (см. `toMessageAttachment`).
 */
type DraftAttachment = TaskAttachment & {
  id: string;
  loading: boolean;
  previewUrl?: string;
  // ATTACHMENT_PROGRESS: доля прочитанного файла (0..1) — из неё рисуется ползунок загрузки.
  progress?: number;
};

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
  // COWORK_BUDGET: добавлено 2026-09-26 — пробрасываем в переключатель моделей платную блокировку
  // Cowork (на десктопе он живёт именно здесь).
  coworkLocked?: boolean;
  onCoworkLockedClick?: () => void;
  isGenerating: boolean;
  onStop: () => void;
  // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
  // onOpenLive: () => void;
  onSend: (prompt: string, attachments: TaskAttachment[]) => Promise<SendOutcome> | SendOutcome | void;
  // FILE_DROP: добавлено 2026-09-24 (L11) — файлы, брошенные на колонку чата; nonce отличает
  // повторный бросок тех же файлов.
  externalFiles?: { files: File[]; nonce: number } | null;
  // FOCUS_MODE: the start screen collapses its greeting while the composer has focus (mobile only).
  onComposerFocus?: () => void;
  onComposerBlur?: () => void;
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
  coworkLocked,
  onCoworkLockedClick,
  isGenerating,
  onStop,
  onSend,
  externalFiles,
  onComposerFocus,
  onComposerBlur,
}: InputBarProps) {
  const { t } = useTranslation();
  // The model picker lives in the chat header on mobile and inline here on desktop.
  const isMobile = useIsMobile();
  // COMPOSER_KEYS: добавлено 2026-09-24 (L10) — на сенсорных/узких экранах Enter переносит строку
  // (многострочный ввод иначе невозможен), отправка — кнопкой.
  const coarsePointer = useMediaQuery('(pointer: coarse)');
  const enterInsertsNewline = isMobile || coarsePointer;
  const [value, setValue] = useState('');
  // ATTACHMENT_UPLOAD: черновик вложения несёт своё состояние. Файл появляется в списке сразу
  // (с превью и спиннером), а base64 появляется позже — до этого отправлять нельзя.
  const [attachments, setAttachments] = useState<DraftAttachment[]>([]);
  const [isRecording, setIsRecording] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [limitHint, setLimitHint] = useState(false);
  // Visible reason why voice input could not start (permission, no device, unsupported…).
  const [micError, setMicError] = useState<string | null>(null);
  // ATTACHMENTS: files the picker refused, so the user is told instead of seeing nothing happen.
  const [attachError, setAttachError] = useState<string | null>(null);

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
  // VOICE_DUP_FIX: окончательно записанный транскрипт за ВСЕ сессии записи (склеивается по перекрытию).
  const committedRef = useRef('');
  // VOICE_DUP_FIX: окончательный текст ТЕКУЩЕЙ сессии; переносится в committedRef ровно один раз.
  const sessionFinalRef = useRef('');
  // Latest not-yet-finalized tail from the current recognizer session.
  const interimRef = useRef('');
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

  // ATTACHMENT_SIZE_LIMIT: единый способ показать/убрать баннер над полем ввода.
  function showAttachError(message: string) {
    setAttachError(message);
    window.setTimeout(() => setAttachError(null), 8000);
  }

  // ATTACHMENT_SIZE_LIMIT: изменено 2026-09-24 (M24) — лимит считается от СЫРОГО размера файлов
  // (40 МБ) и от оценки всего тела запроса: base64 раздувает файлы на треть, и прежние 45 МБ сырых
  // байт превращались в ~60 МБ запроса — больше серверных 55 МБ, то есть 413 уже ПОСЛЕ загрузки.
  function sizeError(pending: TaskAttachment[], prompt: string): string | null {
    const raw = attachmentBytes(pending);
    if (raw > MAX_TOTAL_ATTACHMENT_BYTES || estimateRequestBytes(pending, prompt) > MAX_REQUEST_BYTES) {
      return t('sync.attachmentsOverLimit', {
        size: formatMegabytes(raw),
        limit: formatMegabytes(MAX_TOTAL_ATTACHMENT_BYTES),
      });
    }
    return null;
  }

  async function submit() {
    const prompt = value.trim();
    // ATTACHMENT_ONLY_SEND: отправить можно и одно фото без текста — но не пустое сообщение.
    const hasAttachments = attachments.length > 0;
    if ((!prompt && !hasAttachments) || disabled) return;

    // ATTACHMENT_UPLOAD: пока файлы читаются, отправлять нечего — в теле ушёл бы пустой base64.
    if (uploading) {
      showAttachError(t('input.waitForUpload'));
      return;
    }

    // TURN_GUARD (H6): пока идёт ответ, Enter ничего не отправляет — текст остаётся в поле.
    if (isGenerating) {
      showAttachError(t('sync.waitForReply'));
      return;
    }

    const tooLarge = sizeError(attachments, prompt);
    if (tooLarge) {
      // Ничего не отправляем и ничего не очищаем — пользователь просто убирает часть файлов.
      showAttachError(tooLarge);
      return;
    }

    // Clear optimistically so the composer feels instant, then hand everything back if the
    // request never made it to the model — the user should not have to retype anything.
    const pending = attachments;
    setValue('');
    setAttachments([]);
    if (textareaRef.current) textareaRef.current.style.height = 'auto';

    const outcome = await onSend(prompt, pending);
    if (outcome && outcome.ok === false) {
      // Keep whatever the user typed meanwhile; only an empty composer gets the text back.
      setValue((current) => (current.trim() ? current : prompt));
      setAttachments((current) => (current.length ? current : pending));
      resizeTextarea();
      showAttachError(
        outcome.tooLarge
          ? t('input.attachmentsTooLarge')
          : outcome.reason === 'busy'
            ? t('sync.turnInFlight')
            : outcome.reason === 'forbidden'
              ? t('sync.chatForbidden')
              : outcome.reason === 'loading'
                ? t('sync.loadingChat')
                : t('input.sendFailed'),
      );
    }
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key !== 'Enter' || e.shiftKey) return;
    // COMPOSER_KEYS (L10): Enter, подтверждающий выбор в IME (китайский/японский/корейский ввод),
    // не должен отправлять полунабранный текст. keyCode 229 — то же для Safari/старых Chrome.
    if (e.nativeEvent.isComposing || e.keyCode === 229) return;
    // На телефоне и планшете Enter — новая строка; отправка кнопкой.
    if (enterInsertsNewline) return;
    e.preventDefault();
    void submit();
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
    const rejected: string[] = [];
    // ATTACHMENT_SIZE_LIMIT (M24): файл, который не влезает в лимит сообщения, отклоняется ДО
    // чтения — а не после загрузки всего запроса на сервер.
    const oversized: string[] = [];
    // ATTACHMENT_UPLOAD: снимок текущего списка нужен только для подсчёта объёма и лимита штук;
    // добавление идёт через setAttachments, потому что чтение файла асинхронное и список успевает
    // измениться (пользователь может убрать вложение, пока остальные ещё читаются).
    let rawTotal = attachmentBytes(attachments);
    let count = attachments.length;

    for (const file of Array.from(files)) {
      // BUGFIX_ATTACHMENTS: a refused file used to be dropped without a word, which reads as
      // "the picker ignores .docx in the agent tab". Report it instead of swallowing it.
      if (!skipMimeCheck && !isAllowedMime(file.type, file.name)) {
        rejected.push(file.name);
        continue;
      }
      if (count >= MAX_ATTACHMENTS) {
        setLimitHint(true);
        window.setTimeout(() => setLimitHint(false), 2500);
        break;
      }
      if (rawTotal + file.size > MAX_TOTAL_ATTACHMENT_BYTES) {
        oversized.push(file.name);
        continue;
      }

      // Сразу показываем вложение: у картинок — мгновенное превью из object URL, у остальных —
      // заглушка с именем. Так видно, что файл принят, ещё до чтения его содержимого.
      const draft: DraftAttachment = {
        id: `${Date.now()}-${count}-${file.name}`,
        fileName: file.name,
        contentType: file.type || 'application/octet-stream',
        contentBase64: '',
        loading: true,
        previewUrl: file.type.startsWith('image/') ? URL.createObjectURL(file) : undefined,
        progress: 0,
      };
      count++;
      rawTotal += file.size;
      setAttachments((prev) => [...prev, draft]);

      try {
        const ready = await fileToAttachment(file, (fraction) => {
          // ATTACHMENT_PROGRESS: обновляем только само вложение; остальные могут уже быть готовы.
          setAttachments((prev) =>
            prev.map((a) => (a.id === draft.id ? { ...a, progress: fraction } : a)),
          );
        });
        setAttachments((prev) =>
          prev.map((a) =>
            a.id === draft.id
              ? { ...ready, id: draft.id, loading: false, previewUrl: draft.previewUrl, progress: 1 }
              : a,
          ),
        );
      } catch (err) {
        console.error('[Attachments] failed to read file', file.name, err);
        rejected.push(file.name);
        setAttachments((prev) => prev.filter((a) => a.id !== draft.id));
        if (draft.previewUrl) URL.revokeObjectURL(draft.previewUrl);
      }
    }

    setMenuOpen(false);
    const problems: string[] = [];
    if (rejected.length > 0) problems.push(t('input.unsupportedFiles', { names: rejected.join(', ') }));
    if (oversized.length > 0) {
      problems.push(t('sync.filesOverLimit', {
        names: oversized.join(', '),
        limit: formatMegabytes(MAX_TOTAL_ATTACHMENT_BYTES),
      }));
    }
    if (problems.length > 0) {
      setAttachError(problems.join(' '));
      window.setTimeout(() => setAttachError(null), 8000);
    }
  }

  // ATTACHMENT_UPLOAD: пока хотя бы один файл читается, отправка ждёт — иначе в сообщение ушёл бы
  // пустой (или недописанный) base64, а пользователь видел бы непонятную ошибку сервера.
  const uploading = attachments.some((a) => a.loading);

  /** Убирает вложение и освобождает превью-URL, если оно ещё было временным. */
  function removeAttachment(id: string) {
    setAttachments((prev) => {
      const target = prev.find((a) => a.id === id);
      if (target?.loading && target.previewUrl) URL.revokeObjectURL(target.previewUrl);
      return prev.filter((a) => a.id !== id);
    });
  }

  // FILE_DROP (L11): файлы, брошенные на колонку чата, прикрепляются как выбранные через «+».
  const lastDropNonceRef = useRef<number | null>(null);
  useEffect(() => {
    if (!externalFiles || externalFiles.nonce === lastDropNonceRef.current) return;
    lastDropNonceRef.current = externalFiles.nonce;
    if (disabled) return;
    void addFiles(externalFiles.files);
    // addFiles reads the current attachments; only a new drop should trigger it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [externalFiles]);

  // VOICE_DUP_FIX: текст собирается ЗАНОВО и склеивается с уже записанным ПО ПЕРЕКРЫТИЮ слов.
  // Распознаватель после перезапуска сессии часто расшифровывает ту же фразу заново целиком — её
  // начало совпадает с хвостом записанного, и без склейки получалась «лесенка»
  // «Привет», «Привет как», «Привет как дела»: одна фраза, дописанная несколько раз.
  function applyTranscript() {
    const spoken = mergeTranscript(committedRef.current, sessionText()).trim();
    const base = baseTextRef.current;
    setValue(base ? (spoken ? `${base} ${spoken}` : base) : spoken);
    resizeTextarea();
  }

  /** Текст текущей сессии: окончательная часть + ещё не подтверждённый хвост. */
  function sessionText(): string {
    // VOICE_DUP_FIX: движок иногда присылает хвост, который ПОВТОРЯЕТ уже подтверждённые слова
    // (финал «Привет» + хвост «Привет Как дела»). Простая склейка пробелом давала удвоение, поэтому
    // склеиваем их той же склейкой по перекрытию.
    return mergeTranscript(sessionFinalRef.current, interimRef.current);
  }

  // VOICE_DUP_FIX: склейка по перекрытию (см. mergeTranscript выше), а не сложение строк.

  // VOICE_DUP_FIX: закрывает текущую сессию распознавания и переносит её текст в общий транскрипт
  // РОВНО один раз, склеивая по перекрытию. Раньше на каждом перезапуске хвост вклеивался как есть,
  // а следующая сессия слышала ту же фразу снова — именно так и появлялись повторы.
  function commitSession() {
    committedRef.current = mergeTranscript(committedRef.current, sessionText());
    sessionFinalRef.current = '';
    interimRef.current = '';
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
    // VOICE_DUP_FIX: запись завершена — переносим текст сессии (включая последний хвост) в транскрипт.
    commitSession();
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
    committedRef.current = '';
    sessionFinalRef.current = '';
    interimRef.current = '';
    emptySessionsRef.current = 0;
    sessionHasResultRef.current = false;
    listeningRef.current = true;

    // VOICE_DUP_FIX: каждая сессия распознавания — НОВЫЙ объект SpeechRecognition. Раньше один объект
    // переиспользовался через start() после onend, и на длинном сообщении браузер снова присылал уже
    // распознанные результаты — из-за этого одна и та же фраза повторялась 4+ раз.
    function startSession() {
      // Текст предыдущей сессии уже перенесён в committedRef (commitSession), поэтому и окончательный
      // текст, и хвост сессии начинаются с нуля.
      sessionFinalRef.current = '';
      interimRef.current = '';

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
        // VOICE_DUP_FIX: куски ВНУТРИ одной сессии тоже склеиваем по перекрытию. Chrome на Android
        // отдаёт «накопительные» результаты, где каждый следующий — надмножество предыдущего
        // («Привет», «Привет Как», «Привет Как дела»); простое сложение строк давало удвоение,
        // которое на ПК не воспроизводилось, потому что там куски не пересекаются.
        let sessionFinal = '';
        let interim = '';
        for (let i = 0; i < event.results.length; i++) {
          const result = event.results[i];
          if (result.isFinal) sessionFinal = mergeTranscript(sessionFinal, result[0].transcript);
          else interim = mergeTranscript(interim, result[0].transcript);
        }
        sessionFinalRef.current = sessionFinal;
        interimRef.current = interim;
        applyTranscript();
        console.info('[Voice] result', { sessionFinal, interim });
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

        // VOICE_DUP_FIX: сессию закрывает сам браузер — переносим её текст в общий транскрипт
        // один раз (склейка по перекрытию не даст одной фразе повториться).
        commitSession();
        applyTranscript();

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
          // Новая сессия — новый объект распознавателя (см. VOICE_DUP_FIX выше).
          startSession();
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

    startSession();
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
      {limitHint && (
        <div className="inputbar-limit">{t('input.maxFiles')}</div>
      )}
      {attachError && (
        <div className="inputbar-error" role="alert">
          <span className="inputbar-error__text">{attachError}</span>
          <button
            className="inputbar-error__close"
            onClick={() => setAttachError(null)}
            aria-label={t('common.close')}
            type="button"
          >
            <CloseIcon size={12} />
          </button>
        </div>
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

      <div className={`inputbar ${attachments.length > 0 ? 'inputbar--attachments' : ''}`}>
        {/* ATTACHMENT_UPLOAD: превью живёт ВНУТРИ композера (как в Gemini), а не отдельной полосой
            над ним: так видно, что фото прикреплено к сообщению, а не висит само по себе. */}
        {attachments.length > 0 && (
          <div className="inputbar__attachments">
            {attachments.map((a) => (
              <div key={a.id} className="inputbar-thumb">
                <div className="inputbar-thumb__media">
                  {a.contentType.startsWith('image/') ? (
                    <img
                      src={a.previewUrl ?? `data:${a.contentType};base64,${a.contentBase64}`}
                      alt={a.fileName}
                      className="inputbar-thumb__img"
                    />
                  ) : (
                    <div className="inputbar-thumb__file">📄</div>
                  )}
                </div>
                <div className="inputbar-thumb__name" title={a.fileName}>{a.fileName}</div>
                {a.loading && (
                  <div className="inputbar-thumb__loading" role="status" aria-label={t('input.uploading')}>
                    <span className="inputbar-thumb__spinner" aria-hidden="true" />
                    {/* ATTACHMENT_PROGRESS: ползунок загрузки — видно, что файл ещё пишется. */}
                    <span className="inputbar-thumb__percent" aria-hidden="true">
                      {Math.round((a.progress ?? 0) * 100)}%
                    </span>
                    <div className="inputbar-thumb__bar" aria-hidden="true">
                      <span
                        className="inputbar-thumb__bar-fill"
                        style={{ width: `${Math.round((a.progress ?? 0) * 100)}%` }}
                      />
                    </div>
                  </div>
                )}
                <button
                  className="inputbar-thumb__remove"
                  onClick={() => removeAttachment(a.id)}
                  aria-label={t('input.removeFile', { name: a.fileName })}
                  type="button"
                >
                  <CloseIcon size={11} />
                </button>
              </div>
            ))}
          </div>
        )}

        <div className="inputbar__row">
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
              {/* Camera capture only makes sense on devices with a camera. */}
              {isMobile && (
                <button className="attach-menu__item" onClick={() => cameraRef.current?.click()}>
                  <CameraIcon size={17} /> {t('input.takePhoto')}
                </button>
              )}
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
          accept="image/*,text/*,.pdf,.doc,.docx,.rtf,.odt,.xls,.xlsx,.ppt,.pptx,.csv,.json,.xml,.yaml,.yml,.md,.zip"
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
        {isMobile && (
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
        )}
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
          // KEYBOARD_CLOSE: marks the field the keyboard handler is allowed to blur when the on-screen
          // keyboard is dismissed (a transcript message being edited is a textarea too).
          data-composer-input="true"
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
          onFocus={onComposerFocus}
          onBlur={onComposerBlur}
          enterKeyHint={enterInsertsNewline ? 'enter' : 'send'}
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
            coworkLocked={coworkLocked}
            onCoworkLockedClick={onCoworkLockedClick}
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
            onClick={() => void submit()}
            title={t('common.send')}
            aria-label={t('common.send')}
            className="send-btn"
            disabled={disabled || uploading || (!hasInputText && attachments.length === 0)}
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
    </div>
  );
}
