// LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
// import { useEffect, useRef, useState } from 'react';
// import { recognizeSpeech, synthesizeSpeech } from '../api/conexyApi';
// import { VoiceOrb } from './VoiceOrb';
// import { useVoiceRecording } from '../hooks/useVoiceRecording';
//
// type LiveStatus = 'listening' | 'thinking' | 'speaking';
//
// interface LiveVoiceModalProps {
//   onSendMessage: (text: string) => Promise<string>;
//   onClose: () => void;
// }
//
// const STATUS_LABEL: Record<LiveStatus, string> = {
//   listening: 'Слушаю...',
//   thinking: 'Думаю...',
//   speaking: 'Говорю...',
// };
//
// /**
//  * Fullscreen ChatGPT-style voice mode for ConexyV1-flash. Recording auto-stops on
//  * silence (VAD), is converted to LPCM and recognized via SpeechKit STT, the reply is
//  * sent to the model and then read aloud via SpeechKit TTS before looping back to listen.
//  */
// export function LiveVoiceModal({ onSendMessage, onClose }: LiveVoiceModalProps) {
//   const [status, setStatus] = useState<LiveStatus>('listening');
//   const [error, setError] = useState<string | null>(null);
//
//   const statusRef = useRef<LiveStatus>('listening');
//   const activeRef = useRef(true);
//   const audioRef = useRef<HTMLAudioElement | null>(null);
//   const processingRef = useRef(false);
//
//   function setStatusSafe(next: LiveStatus) {
//     statusRef.current = next;
//     setStatus(next);
//   }
//
//   const { isRecording, volume, start, stop } = useVoiceRecording({
//     onBlob: (blob) => {
//       void handleAudioBlob(blob);
//     },
//   });
//
//   async function handleAudioBlob(blob: Blob) {
//     if (!activeRef.current || processingRef.current) return;
//     processingRef.current = true;
//     setError(null);
//     setStatusSafe('thinking');
//
//     console.log('[LiveVoice] Received blob for STT:', blob.size, 'bytes');
//     try {
//       console.log('[LiveVoice] Decoding to LPCM 16k...');
//       const pcmBlob = await decodeToPcm16kMono(blob);
//       console.log('[LiveVoice] LPCM decoded, size:', pcmBlob.size, 'Sending POST /api/speech/recognize...');
//
//       const text = await recognizeSpeech(pcmBlob);
//       console.log('[LiveVoice] Recognized text:', text);
//
//       if (!text || !text.trim()) {
//         console.warn('[LiveVoice] Empty text from STT');
//         if (activeRef.current) {
//           setError('Не удалось разобрать речь, попробуйте еще раз');
//           resumeListening();
//         }
//         return;
//       }
//
//       const response = await onSendMessage(text);
//       console.log('[LiveVoice] LLM response received:', response);
//       if (!activeRef.current) return;
//
//       console.log('[LiveVoice] Synthesizing TTS response...');
//       const audioBlob = await synthesizeSpeech(response);
//       if (!activeRef.current) return;
//
//       const url = URL.createObjectURL(audioBlob);
//       await playAudio(url);
//     } catch (err) {
//       console.error('[LiveVoice] STT Flow Error:', err);
//       if (activeRef.current) {
//         setError('Не удалось разобрать речь, попробуйте еще раз');
//         resumeListening();
//       }
//     } finally {
//       processingRef.current = false;
//     }
//   }
//
//   function resumeListening() {
//     if (!activeRef.current) return;
//     setStatusSafe('listening');
//     void start().catch(() => {
//       if (activeRef.current) setError('Нет доступа к микрофону');
//     });
//   }
//
//   function playAudio(url: string): Promise<void> {
//     return new Promise((resolve) => {
//       const audio = new Audio(url);
//       audioRef.current = audio;
//       setStatusSafe('speaking');
//
//       const finish = () => {
//         audioRef.current = null;
//         resolve();
//         resumeListening();
//       };
//
//       audio.onended = finish;
//       audio.onerror = finish;
//       void audio.play().catch(finish);
//     });
//   }
//
//   useEffect(() => {
//     activeRef.current = true;
//     void start().catch(() => {
//       if (activeRef.current) setError('Нет доступа к микрофону');
//     });
//
//     return () => {
//       activeRef.current = false;
//       audioRef.current?.pause();
//       audioRef.current = null;
//     };
//     // eslint-disable-next-line react-hooks/exhaustive-deps
//   }, []);
//
//   return (
//     <div className="live-voice">
//       <div className="live-voice__stage">
//         <VoiceOrb
//           status={status}
//           volume={volume}
//           onClick={() => {
//             if (isRecording) {
//               console.log('[LiveVoice] Manual stop via orb click');
//               stop();
//             }
//           }}
//         />
//         <div className="live-voice__status">{error ?? STATUS_LABEL[status]}</div>
//         {!error && <div className="live-voice__hint">Говорите — я слушаю</div>}
//       </div>
//
//       <button className="live-voice__close" onClick={onClose} aria-label="Закрыть голосовой режим">
//         ✕
//       </button>
//     </div>
//   );
// }
//
// /** Decodes a MediaRecorder blob and resamples it to 16 kHz mono 16-bit LPCM. */
// export async function decodeToPcm16kMono(blob: Blob): Promise<Blob> {
//   if (!blob || blob.size === 0) {
//     throw new Error('Blob is empty');
//   }
//
//   const arrayBuffer = await blob.arrayBuffer();
//
//   // 1. Decode the original WebM/Opus at the system's native sample rate.
//   const tempCtx = new (window.AudioContext || (window as any).webkitAudioContext)();
//   let decodedBuffer: AudioBuffer;
//   try {
//     decodedBuffer = await tempCtx.decodeAudioData(arrayBuffer);
//   } finally {
//     await tempCtx.close();
//   }
//
//   // 2. Hardware-resample to 16 kHz mono via OfflineAudioContext (clean, correct downsampling).
//   const targetSampleRate = 16000;
//   const offlineCtx = new OfflineAudioContext(
//     1,
//     Math.ceil(decodedBuffer.duration * targetSampleRate),
//     targetSampleRate,
//   );
//
//   const source = offlineCtx.createBufferSource();
//   source.buffer = decodedBuffer;
//   source.connect(offlineCtx.destination);
//   source.start(0);
//
//   const resampledBuffer = await offlineCtx.startRendering();
//   const channelData = resampledBuffer.getChannelData(0);
//
//   // 3. Pack Float32 into 16-bit little-endian PCM.
//   const pcmBuffer = new ArrayBuffer(channelData.length * 2);
//   const pcmView = new DataView(pcmBuffer);
//   for (let i = 0; i < channelData.length; i++) {
//     const s = Math.max(-1, Math.min(1, channelData[i]));
//     pcmView.setInt16(i * 2, s < 0 ? s * 0x8000 : s * 0x7fff, true);
//   }
//
//   return new Blob([pcmBuffer], { type: 'audio/x-pcm' });
// }
