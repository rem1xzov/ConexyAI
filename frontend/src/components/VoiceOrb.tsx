interface VoiceOrbProps {
  status: 'listening' | 'thinking' | 'speaking';
  /** Normalized loudness 0..1 from the microphone analyser. */
  volume: number;
  /** Optional click handler (used to manually stop recording during listening). */
  onClick?: () => void;
}

/**
 * Organic ChatGPT-style voice orb. Internal gradient blobs drift and mix continuously
 * via CSS keyframes; the whole orb scales with microphone volume for a "breathing" feel.
 */
export function VoiceOrb({ status, volume, onClick }: VoiceOrbProps) {
  // Amplify the (small) RMS value so speech produces a visible, smooth scale change.
  const level = Math.min(1, volume * 4);
  const scale = 1 + level * 0.25;

  return (
    <div className="voice-orb" style={{ transform: `scale(${scale})` }} onClick={onClick}>
      <div className="voice-orb__glow" style={{ opacity: 0.45 + level * 0.55 }} />
      <div className={`voice-orb__core voice-orb__core--${status}`}>
        <div className="voice-orb__blob voice-orb__blob--1" />
        <div className="voice-orb__blob voice-orb__blob--2" />
        <div className="voice-orb__blob voice-orb__blob--3" />
        <div className="voice-orb__blob voice-orb__blob--4" />
      </div>
    </div>
  );
}
