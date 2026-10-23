using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Service;
using ConexyAI.Service.Device;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ConexyAI.Controller;

// DESKTOP_AGENT: добавлено 2026-10-15 — ключи десктопного клиента.
public sealed record CreateDeviceKeyRequest(string? Name);

public sealed record ExchangeDeviceKeyRequest(string Key);

[ApiController]
[Route("api/device")]
public class DeviceController : ControllerBase
{
    private readonly IDeviceKeyService _keys;
    private readonly IUserIntegrationsService _integrations;
    private readonly ILogger<DeviceController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpFactory;
    // WEB_DEVICE: добавлено 2026-10-18
    private readonly IDeviceToolBridge _bridge;
    private readonly IDeviceTaskQueue _deviceQueue;

    // Кэш номера последнего релиза: GitHub API не дёргаем на каждый запрос.
    private static string? _cachedVersion;
    private static DateTime _cachedAtUtc;
    private static readonly SemaphoreSlim VersionLock = new(1, 1);

    // Адрес по умолчанию — последний релиз на GitHub (образ собирается workflow desktop-release). Переопределяется
    // настройкой Desktop:DownloadUrl, если exe лежит в другом месте.
    private const string DefaultDownloadUrl =
        "https://github.com/rem1xzov/ConexyAI/releases/latest/download/conexy-win-x64.exe";

    // MOBILE_APP: APK лежит в том же релизе, что и exe. Прямая ссылка `releases/latest/download`
    // работает только если в последнем релизе есть такой ассет — иначе 404, поэтому клиенту отдаём
    // точную ссылку конкретного ассета.
    private const string MobileAssetName = "conexy-android.apk";
    private const string MobileFallbackUrl =
        "https://github.com/rem1xzov/ConexyAI/releases/latest/download/" + MobileAssetName;

    // Один запрос к GitHub — два ответа: номер последнего релиза и ссылка на APK.
    private sealed record LatestRelease(string? Version, string? MobileUrl);
    private static LatestRelease? _cachedRelease;
    private static DateTime _cachedReleaseAtUtc;

    public DeviceController(
        IDeviceKeyService keys,
        IUserIntegrationsService integrations,
        ILogger<DeviceController> logger,
        IConfiguration configuration,
        IHttpClientFactory httpFactory,
        IDeviceToolBridge bridge,
        IDeviceTaskQueue deviceQueue)
    {
        _bridge = bridge;
        _deviceQueue = deviceQueue;
        _httpFactory = httpFactory;
        _keys = keys;
        _integrations = integrations;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>Ссылка на скачивание клиента: exe для ПК или APK для телефона (`?platform=android`).</summary>
    [HttpGet("client")]
    [AllowAnonymous]
    public async Task<IActionResult> ClientInfo([FromQuery] string? platform, CancellationToken ct)
    {
        var url = _configuration["Desktop:DownloadUrl"];
        var isMobile = string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase);
        return Ok(new
        {
            downloadUrl = isMobile ? await LatestMobileUrlAsync(ct) : string.IsNullOrWhiteSpace(url) ? DefaultDownloadUrl : url,
            platform = isMobile ? "android" : "windows-x64",
            version = await LatestVersionAsync(ct),
            // GOOGLE_CONNECTOR: OAuth-клиент типа «Desktop app» для входа в Google из десктопа. Для таких клиентов
            // Google сам считает secret неконфиденциальным (он вшит в приложение, защита — PKCE), поэтому
            // отдаём его клиенту. Токены пользователя сервер не видит: они остаются на ПК.
            googleClientId = NullIfEmpty(_configuration["Google:DesktopClientId"]),
            googleClientSecret = NullIfEmpty(_configuration["Google:DesktopClientSecret"]),
        });
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Ссылка на APK последнего релиза; при недоступности GitHub — стабильная ссылка-запас.</summary>
    private async Task<string> LatestMobileUrlAsync(CancellationToken ct) =>
        (await LatestReleaseAsync(ct)).MobileUrl ?? MobileFallbackUrl;

    /// <summary>Тег последнего релиза клиента без «v» (например 0.1.5) или null, если GitHub недоступен.</summary>
    private async Task<string?> LatestVersionAsync(CancellationToken ct) =>
        (await LatestReleaseAsync(ct)).Version;

    private async Task<LatestRelease> LatestReleaseAsync(CancellationToken ct)
    {
        var cached = _cachedRelease ?? new LatestRelease(_cachedVersion, null);
        if (DateTime.UtcNow - _cachedAtUtc < TimeSpan.FromMinutes(10))
            return cached;

        await VersionLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _cachedAtUtc < TimeSpan.FromMinutes(10))
                return _cachedRelease ?? new LatestRelease(_cachedVersion, null);

            try
            {
                var http = _httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.github.com/repos/rem1xzov/ConexyAI/releases/latest");
                request.Headers.UserAgent.ParseAdd("ConexyAI-Server");
                using var response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = await System.Text.Json.JsonDocument.ParseAsync(
                        await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    string? version = null;
                    if (doc.RootElement.TryGetProperty("tag_name", out var tag))
                        version = tag.GetString()?.TrimStart('v', 'V');

                    string? mobileUrl = null;
                    if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (asset.TryGetProperty("name", out var name) && name.GetString() == MobileAssetName
                                && asset.TryGetProperty("browser_download_url", out var download))
                            {
                                mobileUrl = download.GetString();
                                break;
                            }
                        }
                    }

                    _cachedVersion = version;
                    _cachedRelease = new LatestRelease(version, mobileUrl);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                _logger.LogWarning("Could not read the latest desktop release: {Message}", ex.Message);
            }

            // При ошибке тоже запоминаем момент, чтобы не бить по GitHub каждый запрос.
            _cachedAtUtc = DateTime.UtcNow;
            return _cachedRelease ?? new LatestRelease(_cachedVersion, null);
        }
        finally
        {
            VersionLock.Release();
        }
    }

    /// <summary>Выпускает ключ для десктопа. Открытый ключ в ответе показывается один раз.</summary>
    [HttpPost("keys")]
    [Authorize]
    public async Task<IActionResult> Issue([FromBody] CreateDeviceKeyRequest? request, CancellationToken ct)
    {
        if (IsDeviceSession())
            return Forbid403();

        try
        {
            var issued = await _keys.IssueAsync(User.GetUserId(), request?.Name, ct);
            _logger.LogInformation("Device key issued: user={UserId} key={KeyId}", User.GetUserId(), issued.Info.Id);
            return Ok(new { key = issued.Key, info = issued.Info });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "DEVICE_KEY_LIMIT", message = ex.Message });
        }
    }

    // Токен из обмена ключа не управляет ключами: утечка ключа не позволяет выпустить себе новые.
    private bool IsDeviceSession() =>
        User.FindFirst(ITokenService.DeviceSourceClaim)?.Value == ITokenService.DeviceSourceValue;

    private ObjectResult Forbid403() => StatusCode(StatusCodes.Status403Forbidden,
        new { error = "DEVICE_SESSION_FORBIDDEN", message = "Ключами нельзя управлять из десктопной сессии." });

    /// <summary>
    /// GitHub-токен с сайта для git push/pull на ПК. Отдаётся ТОЛЬКО десктопной сессии (токен из обмена ключа):
    /// браузерная сессия сайта его не получит, как и раньше (сервер отдаёт только статус).
    /// </summary>
    [HttpGet("github-credential")]
    [Authorize]
    public async Task<IActionResult> GitHubCredential(CancellationToken ct)
    {
        if (!IsDeviceSession())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "DEVICE_SESSION_REQUIRED" });

        var stored = await _integrations.GetForRunAsync(User.GetUserId(), ct);
        Response.Headers.CacheControl = "no-store";
        return string.IsNullOrWhiteSpace(stored.GitHubToken)
            ? NoContent()
            : Ok(new { token = stored.GitHubToken });
    }

    // ---- WEB_DEVICE: добавлено 2026-10-18 — состояние ПК и очередь задач для веба ----

    /// <summary>Подключён ли ПК, его проекты и задачи, ждущие его подключения.</summary>
    [HttpGet("status")]
    [Authorize]
    public IActionResult Status()
    {
        var userId = User.GetUserId();
        var info = _bridge.GetInfo(userId);
        return Ok(new
        {
            online = info is not null,
            name = info?.Name,
            os = info?.Os,
            workspace = info?.Workspace,
            projects = (info?.Projects ?? Array.Empty<DeviceProject>()).Select(p => new { id = p.Id, name = p.Name }),
            queued = _deviceQueue.List(userId).Select(q => new
            {
                taskId = q.Job.TaskId,
                chatId = q.Job.ChatId,
                prompt = q.Job.Prompt.Length <= 200 ? q.Job.Prompt : q.Job.Prompt[..200],
                createdAt = q.CreatedAt,
            }),
        });
    }

    /// <summary>Выполнить ждущую ПК задачу в песочнице.</summary>
    [HttpPost("queue/{taskId:guid}/sandbox")]
    [Authorize]
    public async Task<IActionResult> QueueToSandbox(Guid taskId, CancellationToken ct) =>
        await _deviceQueue.MoveToSandboxAsync(User.GetUserId(), taskId, ct) ? NoContent() : NotFound();

    /// <summary>Отменить ждущую ПК задачу.</summary>
    [HttpDelete("queue/{taskId:guid}")]
    [Authorize]
    public async Task<IActionResult> CancelQueued(Guid taskId, CancellationToken ct) =>
        await _deviceQueue.CancelAsync(User.GetUserId(), taskId, ct) ? NoContent() : NotFound();

    [HttpGet("keys")]
    [Authorize]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await _keys.ListAsync(User.GetUserId(), ct));

    [HttpDelete("keys/{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        if (IsDeviceSession())
            return Forbid403();

        if (!await _keys.RevokeAsync(User.GetUserId(), id, ct))
            return NotFound();

        _logger.LogInformation("Device key revoked: user={UserId} key={KeyId}", User.GetUserId(), id);
        return NoContent();
    }

    /// <summary>
    /// Десктоп меняет свой ключ на обычный JWT и дальше работает через тот же SignalR-хаб и API.
    /// Ответ одинаков для неверного и отозванного ключа — перебор ничего не раскрывает.
    /// </summary>
    [HttpPost("token")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.DeviceToken)]
    public async Task<ActionResult<TokenResponse>> Exchange([FromBody] ExchangeDeviceKeyRequest request, CancellationToken ct)
    {
        var token = await _keys.ExchangeAsync(request.Key, ct);
        return token is null
            ? Unauthorized(new { error = "INVALID_DEVICE_KEY", message = "Ключ недействителен или отозван." })
            : Ok(token);
    }
}
