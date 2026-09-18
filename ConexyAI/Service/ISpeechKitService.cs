namespace ConexyAI.Service;

/// <summary>
/// Result of a SpeechKit STT call. <see cref="Success"/> is false when the request failed,
/// was not configured, timed out, or the audio contained no recognizable speech.
/// </summary>
public record SpeechRecognitionResult(bool Success, string? Text = null, string? Error = null);

public interface ISpeechKitService
{
    /// <summary>
    /// Recognizes speech from raw audio bytes. <paramref name="audio"/> is expected to be
    /// 16 kHz mono 16-bit LPCM (the format produced by the frontend) or OggOpus.
    /// </summary>
    Task<SpeechRecognitionResult> RecognizeAsync(byte[] audio, CancellationToken ct = default);

    /// <summary>
    /// Synthesizes Russian speech for <paramref name="text"/> and returns a WAV file
    /// (16-bit PCM). Long text is chunked and the resulting audio is concatenated.
    /// </summary>
    Task<byte[]> SynthesizeAsync(string text, CancellationToken ct = default);
}
