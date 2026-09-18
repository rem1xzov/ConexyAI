using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

/// <summary>
/// Live voice mode (only used for <c>ConexyV1-flash</c>): speech-to-text and text-to-speech
/// backed by Yandex SpeechKit.
/// </summary>
[ApiController]
[Route("api/speech")]
[Authorize]
public class SpeechController : ControllerBase
{
    private readonly ISpeechKitService _speechKit;
    private readonly ILogger<SpeechController> _logger;

    public SpeechController(ISpeechKitService speechKit, ILogger<SpeechController> logger)
    {
        _speechKit = speechKit;
        _logger = logger;
    }

    /// <summary>
    /// Recognizes speech from an uploaded audio clip. Accepts either a raw PCM body
    /// (Content-Type: audio/x-pcm) or a multipart upload with a single file. The client
    /// sends 16 kHz mono 16-bit LPCM (the format the frontend converts the mic into).
    /// </summary>
    // LIVE_VOICE_DISABLED: закомментировано временно, см. 2026-09-17
    /*
    [HttpPost("recognize")]
    [RequestSizeLimit(2_000_000)] // ~2MB, comfortably above the 30s/16kHz/16-bit STT limit
    public async Task<IActionResult> Recognize(CancellationToken ct)
    {
        byte[] audio;

        if (Request.HasFormContentType && Request.Form.Files.Count > 0)
        {
            var file = Request.Form.Files[0];
            await using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms, ct);
                audio = ms.ToArray();
            }
        }
        else
        {
            await using (var ms = new MemoryStream())
            {
                await Request.Body.CopyToAsync(ms, ct);
                audio = ms.ToArray();
            }
        }

        if (audio.Length == 0)
            return BadRequest(new { error = "Аудиопоток пуст (0 байт). Проверьте захват MediaRecorder." });

        SpeechRecognitionResult result;
        try
        {
            result = await _speechKit.RecognizeAsync(audio, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("SpeechKit not configured: {Error}", ex.Message);
            return StatusCode(503, new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Yandex STT API failed: {Message}", ex.Message);
            var status = ex.StatusCode ?? System.Net.HttpStatusCode.BadRequest;
            return StatusCode((int)status, new { error = ex.Message });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout fired (TaskCanceledException derives from OperationCanceledException).
            _logger.LogWarning("Yandex STT timed out.");
            return StatusCode(504, new { error = "Yandex STT timeout" });
        }

        if (!result.Success)
            return BadRequest(new { error = result.Error });

        return Ok(new { text = result.Text ?? string.Empty });
    }
    */

    /// <summary>
    /// Synthesizes speech from text and returns a WAV file (16-bit PCM). Long text is chunked
    /// server-side and concatenated into a single audio file.
    /// </summary>
    [HttpPost("synthesize")]
    public async Task<IActionResult> Synthesize([FromBody] SynthesizeRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "Text is required." });

        const int MaxTextLength = 20_000;
        if (request.Text.Length > MaxTextLength)
            return BadRequest(new { error = $"Text exceeds the {MaxTextLength} character limit." });

        try
        {
            var wav = await _speechKit.SynthesizeAsync(request.Text, ct);
            return File(wav, "audio/wav");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Speech synthesis unavailable: {Error}", ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Speech synthesis upstream failure.");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }
}

public record SynthesizeRequest(string Text);
