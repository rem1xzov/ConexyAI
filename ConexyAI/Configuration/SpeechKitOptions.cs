namespace ConexyAI.Configuration;

/// <summary>
/// Binds the <c>SpeechKit</c> section of appsettings.json. Backs the live voice mode
/// (STT + TTS) for <c>ConexyV1-flash</c>.
/// </summary>
public class SpeechKitOptions
{
    public const string SectionName = "SpeechKit";

    /// <summary>API key of the Yandex Cloud service account used for SpeechKit calls.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Yandex Cloud folder id the service account belongs to.</summary>
    public string FolderId { get; set; } = string.Empty;

    public string SttEndpoint { get; set; } = "https://stt.api.cloud.yandex.net/speech/v1/stt:recognize";
    public string TtsEndpoint { get; set; } = "https://tts.api.cloud.yandex.net/speech/v1/tts:synthesize";

    /// <summary>Default Russian TTS voice (see Yandex SpeechKit voice catalogue).</summary>
    public string TtsVoice { get; set; } = "alena";

    /// <summary>SpeechKit TTS v1 accepts up to 5000 chars per request; we chunk below that.</summary>
    public int TtsMaxCharsPerRequest { get; set; } = 4000;

    /// <summary>Timeout for a single upstream SpeechKit HTTP call.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
