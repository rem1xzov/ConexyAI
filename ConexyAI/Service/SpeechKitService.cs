using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ConexyAI.Configuration;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

/// <summary>
/// Thin client for Yandex SpeechKit v1 (synchronous STT and TTS). Authentication uses the
/// service-account API key in the <c>Authorization: Api-Key ...</c> header together with the
/// <c>x-folder-id</c> header.
/// </summary>
public class SpeechKitService : ISpeechKitService
{
    // The frontend records and converts the mic to 16 kHz mono 16-bit LPCM before uploading.
    private const int SttSampleRate = 16000;
    // TTS v1 LPCM output is 16-bit mono at 48 kHz.
    private const int TtsSampleRate = 48000;
    private const int TtsChannels = 1;
    private const int TtsBitsPerSample = 16;
    // PRIVACY_LOGS: добавлено 2026-09-24 (ревью H3) — тела ответов апстрима в логах обрезаются.
    private const int MaxLoggedBodyChars = 300;

    private readonly HttpClient _httpClient;
    private readonly SpeechKitOptions _options;
    private readonly ILogger<SpeechKitService> _logger;

    public SpeechKitService(
        HttpClient httpClient,
        IOptions<SpeechKitOptions> options,
        ILogger<SpeechKitService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SpeechRecognitionResult> RecognizeAsync(byte[] audio, CancellationToken ct = default)
    {
        var configError = ValidateConfig();
        if (configError is not null)
            throw new InvalidOperationException(configError);

        if (audio is null || audio.Length == 0)
            return new SpeechRecognitionResult(false, Error: "Audio is empty.");

        // A very short recording cannot contain meaningful speech.
        if (audio.Length < SttSampleRate * 2 / 3) // < ~0.33s of 16-bit mono PCM
            return new SpeechRecognitionResult(false, Error: "Recording is too short or silent.");

        var url = $"{_options.SttEndpoint.TrimEnd('/')}?folderId={Uri.EscapeDataString(_options.FolderId)}&lang=ru-RU&format=lpcm&sampleRateHertz={SttSampleRate}";

        using var content = new ByteArrayContent(audio);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/x-pcm");

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Authorization", $"Api-Key {_options.ApiKey}");
        request.Headers.Add("x-folder-id", _options.FolderId);
        request.Content = content;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        stopwatch.Stop();

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        // PRIVACY_LOGS: добавлено 2026-09-24 (ревью H3) — тело успешного ответа STT — это распознанная
        // речь пользователя; в лог идут только статус, время и длина.
        _logger.LogInformation("Yandex STT responded in {Elapsed} ms with status {Status} ({BodyLength} chars).",
            stopwatch.ElapsedMilliseconds, (int)response.StatusCode, responseBody.Length);

        if (!response.IsSuccessStatusCode)
        {
            var errorPreview = Truncate(responseBody);
            _logger.LogError("SpeechKit STT failed: {Status} {Body}", (int)response.StatusCode, errorPreview);
            throw new HttpRequestException($"Yandex error [{(int)response.StatusCode}]: {errorPreview}", null, response.StatusCode);
        }

        var parsed = JsonSerializer.Deserialize<SttResponse>(responseBody);
        var text = parsed?.Result?.Trim();

        if (string.IsNullOrWhiteSpace(text))
            return new SpeechRecognitionResult(false, Error: "No speech detected.");

        return new SpeechRecognitionResult(true, Text: text);
    }

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        var configError = ValidateConfig();
        if (configError is not null)
            throw new InvalidOperationException(configError);

        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text to synthesize is empty.");

        var chunks = ChunkText(text, _options.TtsMaxCharsPerRequest);
        using var pcm = new MemoryStream();

        foreach (var chunk in chunks)
        {
            var audio = await SynthesizeChunkAsync(chunk, ct);
            await pcm.WriteAsync(audio, ct);
        }

        return WrapPcmInWav(pcm.ToArray());
    }

    private async Task<byte[]> SynthesizeChunkAsync(string text, CancellationToken ct)
    {
        // SPEECH_HARDENING: добавлено 2026-09-24 (ревью L7) — раньше все параметры, включая сам текст,
        // шли в query string POST-запроса (URL до ~24 КБ, текст пользователя — в логах прокси/апстрима).
        // SpeechKit v1 tts:synthesize принимает те же параметры телом application/x-www-form-urlencoded.
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TtsEndpoint);
        AddAuthHeaders(request);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["text"] = text,
            ["lang"] = "ru-RU",
            ["voice"] = _options.TtsVoice,
            ["format"] = "lpcm",
            ["sampleRateHertz"] = TtsSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["folderId"] = _options.FolderId
        });

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("SpeechKit TTS timed out after {Timeout}s.", _options.TimeoutSeconds);
            throw new HttpRequestException($"Speech synthesis timed out after {_options.TimeoutSeconds} seconds.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                // PRIVACY_LOGS: 2026-09-24 (ревью H3) — тело ошибки может повторять текст; только начало.
                _logger.LogError("SpeechKit TTS failed: {Status} {Body}", (int)response.StatusCode, Truncate(body));
                throw new HttpRequestException($"Speech synthesis failed (HTTP {(int)response.StatusCode}).");
            }

            return await response.Content.ReadAsByteArrayAsync(ct);
        }
    }

    /// <summary>At most <see cref="MaxLoggedBodyChars"/> characters of an upstream error body, for logs.</summary>
    private static string Truncate(string? body) =>
        string.IsNullOrEmpty(body) || body.Length <= MaxLoggedBodyChars
            ? body ?? string.Empty
            : body[..MaxLoggedBodyChars] + $"…(+{body.Length - MaxLoggedBodyChars} chars)";

    private string? ValidateConfig()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return "SpeechKit is not configured (missing SpeechKit:ApiKey).";
        if (string.IsNullOrWhiteSpace(_options.FolderId))
            return "SpeechKit is not configured (missing SpeechKit:FolderId).";
        return null;
    }

    private void AddAuthHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Authorization", $"Api-Key {_options.ApiKey}");
        request.Headers.TryAddWithoutValidation("x-folder-id", _options.FolderId);
    }

    private static IReadOnlyList<string> ChunkText(string text, int maxChars)
    {
        if (maxChars <= 0) maxChars = 4000;
        if (text.Length <= maxChars) return new[] { text };

        var chunks = new List<string>();
        var remaining = text;
        while (remaining.Length > maxChars)
        {
            // Prefer breaking at whitespace near the boundary so words are not split.
            var cut = remaining.LastIndexOfAny(new[] { ' ', '\n', '\r', '.', '!', '?', ',', ';', ':', '-' }, maxChars - 1);
            if (cut <= 0) cut = maxChars;
            chunks.Add(remaining[..cut].Trim());
            remaining = remaining[cut..].TrimStart();
        }

        if (remaining.Length > 0)
            chunks.Add(remaining.Trim());

        return chunks.Where(c => c.Length > 0).ToList();
    }

    private static byte[] WrapPcmInWav(byte[] pcm)
    {
        var dataSize = pcm.Length;
        var byteRate = TtsSampleRate * TtsChannels * (TtsBitsPerSample / 8);
        var blockAlign = TtsChannels * (TtsBitsPerSample / 8);

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            // RIFF header
            w.Write("RIFF"u8);
            w.Write(36 + dataSize);
            w.Write("WAVE"u8);

            // fmt sub-chunk
            w.Write("fmt "u8);
            w.Write(16); // PCM fmt chunk size
            w.Write((short)1); // PCM format
            w.Write((short)TtsChannels);
            w.Write(TtsSampleRate);
            w.Write(byteRate);
            w.Write((short)blockAlign);
            w.Write((short)TtsBitsPerSample);

            // data sub-chunk
            w.Write("data"u8);
            w.Write(dataSize);
            w.Write(pcm);
        }

        return ms.ToArray();
    }

    private sealed class SttResponse
    {
        // 2026-09-24: Yandex отвечает {"result": "..."}; без явного имени System.Text.Json
        // (регистрозависимый по умолчанию) не связывал поле, и распознавание всегда было пустым.
        [System.Text.Json.Serialization.JsonPropertyName("result")]
        public string? Result { get; set; }
    }
}
