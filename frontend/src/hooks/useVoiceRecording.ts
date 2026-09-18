// LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
// import { useCallback, useEffect, useRef, useState } from 'react';
//
// const SILENCE_MS = 1600;
// const MAX_DURATION_MS = 25000;
// const SPEECH_THRESHOLD = 0.06;
// const VOLUME_UPDATE_INTERVAL_MS = 50;
// const TIMESLICE_MS = 250;
//
// interface UseVoiceRecordingOptions {
//   /** Called once the recording stops (auto or forced) with the final audio blob. */
//   onBlob: (blob: Blob) => void;
// }
//
// interface UseVoiceRecordingResult {
//   isRecording: boolean;
//   /** Normalized loudness (RMS) 0..1, throttled to ~20 fps. */
//   volume: number;
//   start: () => Promise<void>;
//   stop: () => void;
// }
//
// /**
//  * Mic recording with silence detection (VAD). Streams the mic into a MediaRecorder
//  * and an AnalyserNode; once speech has started and then falls silent for
//  * <c>SILENCE_MS</c> (or after <c>MAX_DURATION_MS</c>), the recorder is stopped and
//  * <c>onBlob</c> is invoked.
//  */
// export function useVoiceRecording({ onBlob }: UseVoiceRecordingOptions): UseVoiceRecordingResult {
//   const [isRecording, setIsRecording] = useState(false);
//   const [volume, setVolume] = useState(0);
//
//   const mediaRecorderRef = useRef<MediaRecorder | null>(null);
//   const chunksRef = useRef<Blob[]>([]);
//   const streamRef = useRef<MediaStream | null>(null);
//   const audioContextRef = useRef<AudioContext | null>(null);
//   const analyserRef = useRef<AnalyserNode | null>(null);
//
//   const rafRef = useRef<number | null>(null);
//   const silenceTimerRef = useRef<number | null>(null);
//   const maxTimerRef = useRef<number | null>(null);
//   const speechDetectedRef = useRef(false);
//   const disposedRef = useRef(false);
//
//   // Keep the latest callback in a ref so MediaRecorder's onstop never captures a stale
//   // closure across re-renders (prevents the zombie-recorder / lost-onBlob bug).
//   const onBlobRef = useRef(onBlob);
//   useEffect(() => {
//     onBlobRef.current = onBlob;
//   }, [onBlob]);
//
//   const cancelVad = useCallback(() => {
//     if (rafRef.current !== null) {
//       cancelAnimationFrame(rafRef.current);
//       rafRef.current = null;
//     }
//     if (silenceTimerRef.current !== null) {
//       window.clearTimeout(silenceTimerRef.current);
//       silenceTimerRef.current = null;
//     }
//     if (maxTimerRef.current !== null) {
//       window.clearTimeout(maxTimerRef.current);
//       maxTimerRef.current = null;
//     }
//   }, []);
//
//   const teardown = useCallback(() => {
//     cancelVad();
//     if (audioContextRef.current) {
//       void audioContextRef.current.close();
//       audioContextRef.current = null;
//     }
//     analyserRef.current = null;
//     streamRef.current?.getTracks().forEach((t) => {
//       t.stop();
//       t.enabled = false;
//     });
//     streamRef.current = null;
//     setVolume(0);
//   }, [cancelVad]);
//
//   const stop = useCallback(() => {
//     const recorder = mediaRecorderRef.current;
//     mediaRecorderRef.current = null;
//     setIsRecording(false);
//
//     if (recorder && recorder.state !== 'inactive') {
//       recorder.stop();
//     }
//
//     // Hard reset the mic immediately so no zombie recorder keeps the mic alive.
//     teardown();
//   }, [teardown]);
//
//   const start = useCallback(async () => {
//     // Always start with a clean buffer so chunks from a previous session never leak
//     // into (and corrupt) the next WebM blob.
//     chunksRef.current = [];
//
//     // Guard against a zombie recorder: never start a second recording while one is active.
//     if (mediaRecorderRef.current && mediaRecorderRef.current.state !== 'inactive') {
//       console.log('[Voice] Already recording, skipping start');
//       return;
//     }
//
//     if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined') {
//       throw new Error('Микрофон недоступен в этом браузере');
//     }
//
//     disposedRef.current = false;
//     const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
//     streamRef.current = stream;
//
//     const mimeType = pickMimeType();
//     const recorder = new MediaRecorder(stream, mimeType ? { mimeType } : undefined);
//
//     recorder.ondataavailable = (e) => {
//       if (e.data && e.data.size > 0) {
//         chunksRef.current.push(e.data);
//         console.log('[Voice] Chunk received, size:', e.data.size);
//       }
//     };
//
//     recorder.onstop = () => {
//       console.log('[Voice] Recorder onstop triggered');
//       const audioBlob = new Blob(chunksRef.current, { type: recorder.mimeType || 'audio/webm' });
//       console.log('[Voice] Final blob size:', audioBlob.size);
//
//       if (!disposedRef.current && audioBlob.size > 0 && onBlobRef.current) {
//         onBlobRef.current(audioBlob);
//       } else if (audioBlob.size === 0) {
//         console.warn('[Voice] Audio blob is empty, skipping recognize');
//       }
//     };
//
//     mediaRecorderRef.current = recorder;
//
//     const audioContext = new AudioContext();
//     const source = audioContext.createMediaStreamSource(stream);
//     const analyser = audioContext.createAnalyser();
//     analyser.fftSize = 256;
//     source.connect(analyser);
//     audioContextRef.current = audioContext;
//     analyserRef.current = analyser;
//
//     recorder.start(TIMESLICE_MS);
//     setIsRecording(true);
//     speechDetectedRef.current = false;
//
//     startVadLoop(analyser);
//     maxTimerRef.current = window.setTimeout(() => stop(), MAX_DURATION_MS);
//   }, [stop, teardown]);
//
//   function startVadLoop(analyser: AnalyserNode) {
//     const data = new Uint8Array(analyser.fftSize);
//     let lastVolumeAt = 0;
//
//     const tick = () => {
//       const currentAnalyser = analyserRef.current;
//       if (!currentAnalyser) return;
//
//       currentAnalyser.getByteTimeDomainData(data);
//
//       let sum = 0;
//       for (let i = 0; i < data.length; i++) {
//         const v = (data[i] - 128) / 128;
//         sum += v * v;
//       }
//       const rms = Math.sqrt(sum / data.length);
//
//       const now = performance.now();
//       if (now - lastVolumeAt >= VOLUME_UPDATE_INTERVAL_MS) {
//         lastVolumeAt = now;
//         setVolume(rms);
//       }
//
//       if (rms > SPEECH_THRESHOLD) {
//         if (!speechDetectedRef.current) speechDetectedRef.current = true;
//         if (silenceTimerRef.current !== null) {
//           window.clearTimeout(silenceTimerRef.current);
//           silenceTimerRef.current = null;
//         }
//       } else if (speechDetectedRef.current && silenceTimerRef.current === null) {
//         silenceTimerRef.current = window.setTimeout(() => {
//           stop();
//         }, SILENCE_MS);
//       }
//
//       rafRef.current = requestAnimationFrame(tick);
//     };
//
//     rafRef.current = requestAnimationFrame(tick);
//   }
//
//   useEffect(() => {
//     return () => {
//       disposedRef.current = true;
//       const recorder = mediaRecorderRef.current;
//       if (recorder) {
//         recorder.onstop = null;
//         if (recorder.state !== 'inactive') recorder.stop();
//       }
//       mediaRecorderRef.current = null;
//       teardown();
//     };
//   }, [teardown]);
//
//   return { isRecording, volume, start, stop };
// }
//
// function pickMimeType(): string | undefined {
//   if (typeof MediaRecorder.isTypeSupported !== 'function') return undefined;
//   const candidates = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus', 'audio/ogg'];
//   return candidates.find((c) => MediaRecorder.isTypeSupported(c));
// }
