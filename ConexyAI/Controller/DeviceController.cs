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
    private readonly Microsoft.Extensions.Options.IOptions<ConexyAI.Configuration.AgentProxyOptions> _proxy;
    private readonly IDeviceTaskQueue _deviceQueue;

    // Кэш номеров последних релизов: GitHub API не дёргаем на каждый запрос.
    private static readonly SemaphoreSlim VersionLock = new(1, 1);

    // Адрес по умолчанию — последний релиз на GitHub (образ собирается workflow desktop-release). Переопределяется
    // настройкой Desktop:DownloadUrl, если exe лежит в другом месте.
    private const string DefaultDownloadUrl =
        "https://github.com/rem1xzov/ConexyAI/releases/latest/download/conexy-win-x64.exe";

    private const string DesktopAssetName = "conexy-win-x64.exe";

    // MOBILE_APP: у мобильного клиента свой релиз и свой номер версии. Десктоп и приложение
    // обновляются независимо, поэтому берём не «последний релиз вообще», а последний,
    // в котором есть нужный ассет: иначе релиз с одним APK ломал бы обновление ПК и наоборот.
    private const string MobileAssetName = "conexy-android.apk";
    private const string MobileFallbackUrl =
        "https://github.com/rem1xzov/ConexyAI/releases/latest/download/" + MobileAssetName;

    // Один запрос к GitHub — версии и ссылки обоих клиентов.
    private sealed record LatestReleases(string? DesktopVersion, string? DesktopUrl, string? MobileVersion, string? MobileUrl);
    private static LatestReleases? _cachedReleases;
    private static DateTime _cachedReleasesAtUtc;

    public DeviceController(
        IDeviceKeyService keys,
        IUserIntegrationsService integrations,
        ILogger<DeviceController> logger,
        IConfiguration configuration,
        IHttpClientFactory httpFactory,
        IDeviceToolBridge bridge,
        IDeviceTaskQueue deviceQueue,
        Microsoft.Extensions.Options.IOptions<ConexyAI.Configuration.AgentProxyOptions> proxy)
    {
        _proxy = proxy;
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
        var releases = await LatestReleasesAsync(ct);
        var isMobile = string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase);
        var configured = _configuration["Desktop:DownloadUrl"];
        var desktopUrl = string.IsNullOrWhiteSpace(configured)
            ? releases.DesktopUrl ?? DefaultDownloadUrl
            : configured;
        return Ok(new
        {
            downloadUrl = isMobile ? releases.MobileUrl ?? MobileFallbackUrl : desktopUrl,
            platform = isMobile ? "android" : "windows-x64",
            version = isMobile ? releases.MobileVersion : releases.DesktopVersion,
            // GOOGLE_CONNECTOR: OAuth-клиент типа «Desktop app» для входа в Google из десктопа. Для таких клиентов
            // Google сам считает secret неконфиденциальным (он вшит в приложение, защита — PKCE), поэтому
            // отдаём его клиенту. Токены пользователя сервер не видит: они остаются на ПК.
            googleClientId = NullIfEmpty(_configuration["Google:DesktopClientId"]),
            googleClientSecret = NullIfEmpty(_configuration["Google:DesktopClientSecret"]),
        });
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<LatestReleases> LatestReleasesAsync(CancellationToken ct)
    {
        if (_cachedReleases is not null && DateTime.UtcNow - _cachedReleasesAtUtc < TimeSpan.FromMinutes(10))
            return _cachedReleases;

        await VersionLock.WaitAsync(ct);
        try
        {
            if (_cachedReleases is not null && DateTime.UtcNow - _cachedReleasesAtUtc < TimeSpan.FromMinutes(10))
                return _cachedReleases;

            try
            {
                var http = _httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.github.com/repos/rem1xzov/ConexyAI/releases?per_page=20");
                request.Headers.UserAgent.ParseAdd("ConexyAI-Server");
                using var response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = await System.Text.Json.JsonDocument.ParseAsync(
                        await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        _cachedReleases = ReadReleases(doc.RootElement);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                _logger.LogWarning("Could not read the latest releases: {Message}", ex.Message);
            }

            // При ошибке тоже запоминаем момент, чтобы не бить по GitHub каждый запрос.
            _cachedReleasesAtUtc = DateTime.UtcNow;
            return _cachedReleases ?? new LatestReleases(null, null, null, null);
        }
        finally
        {
            VersionLock.Release();
        }
    }

    // Релизы приходят от новых к старым: для каждого клиента запоминаем первый релиз с его ассетом.
    private static LatestReleases ReadReleases(System.Text.Json.JsonElement releases)
    {
        string? desktopVersion = null, desktopUrl = null, mobileVersion = null, mobileUrl = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == System.Text.Json.JsonValueKind.True)
                continue;
            if (!release.TryGetProperty("tag_name", out var tag))
                continue;
            var version = tag.GetString()?.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(version))
                continue;
            if (!release.TryGetProperty("assets", out var assets)
                || assets.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !asset.TryGetProperty("name", out var name)
                    || !asset.TryGetProperty("browser_download_url", out var download))
                    continue;
                var url = download.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    continue;
                var assetName = name.GetString();
                if (assetName == DesktopAssetName && desktopVersion is null)
                {
                    desktopVersion = version;
                    desktopUrl = url;
                }
                else if (assetName == MobileAssetName && mobileVersion is null)
                {
                    mobileVersion = version;
                    mobileUrl = url;
                }
            }

            if (desktopVersion is not null && mobileVersion is not null)
                break;
        }

        return new LatestReleases(desktopVersion, desktopUrl, mobileVersion, mobileUrl);
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

    // ---- TELEGRAM_LOGIN: подключение Telegram (личный аккаунт) из настроек сайта и телефона ----
    // Telegram MCP работает на ПК пользователя, поэтому вход тоже идёт там: сервер только передаёт
    // команды подключённому клиенту и ничего не хранит (ключи и сессия живут зашифрованными на ПК).

    /// <summary>
    /// TELEGRAM_PROXY: прокси сервера (AGENT_PROXY_URL) для Telegram на ПК. В нём логин и пароль платного прокси,
    /// поэтому отдаётся только администратору; остальные задают свой на ПК (/proxy).
    /// </summary>
    [HttpGet("proxy")]
    [Authorize]
    public IActionResult AgentProxy()
    {
        if (!User.IsAdmin() || !_proxy.Value.IsConfigured)
            return NoContent();
        return Ok(new { url = _proxy.Value.Url.Trim() });
    }

    public sealed record TelegramStartRequest(string? ApiId, string? ApiHash);

    public sealed record TelegramPasswordRequest(string? Password);

    [HttpPost("telegram/start")]
    [Authorize]
    public Task<IActionResult> TelegramStart([FromBody] TelegramStartRequest body, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(body.ApiId) || string.IsNullOrWhiteSpace(body.ApiHash)
            ? Task.FromResult<IActionResult>(BadRequest(new { error = "Нужны api_id и api_hash." }))
            : TelegramCallAsync("telegram_login_start", new { api_id = body.ApiId.Trim(), api_hash = body.ApiHash.Trim() }, ct);

    [HttpGet("telegram/status")]
    [Authorize]
    public Task<IActionResult> TelegramStatus(CancellationToken ct) =>
        TelegramCallAsync("telegram_login_status", new { wait_seconds = 20 }, ct);

    [HttpPost("telegram/password")]
    [Authorize]
    public Task<IActionResult> TelegramPassword([FromBody] TelegramPasswordRequest body, CancellationToken ct) =>
        string.IsNullOrEmpty(body.Password)
            ? Task.FromResult<IActionResult>(BadRequest(new { error = "Введите пароль." }))
            : TelegramCallAsync("telegram_login_password", new { password = body.Password }, ct);

    [HttpPost("telegram/cancel")]
    [Authorize]
    public Task<IActionResult> TelegramCancel(CancellationToken ct) =>
        TelegramCallAsync("telegram_login_cancel", new { }, ct);

    private async Task<IActionResult> TelegramCallAsync(string tool, object args, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!_bridge.IsOnline(userId))
            return Conflict(new { error = "ПК не подключён. Telegram работает через Conexy на вашем ПК: запустите его (или включите фоновую работу: /daemon on) и повторите." });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        DeviceToolOutcome outcome;
        try
        {
            outcome = await _bridge.ExecuteAsync(userId, Guid.Empty, tool, System.Text.Json.JsonSerializer.Serialize(args), timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(504, new { error = "ПК не ответил вовремя. Проверьте, что Conexy на ПК запущен и обновлён." });
        }

        if (outcome.IsError)
        {
            // Старый клиент не знает этих команд.
            var message = outcome.Output.Contains("не поддерживается", StringComparison.Ordinal) || outcome.Output.Contains("проект", StringComparison.OrdinalIgnoreCase)
                ? "Обновите Conexy на ПК (/update) — вход в Telegram из настроек появился в новой версии."
                : outcome.Output;
            return BadRequest(new { error = message });
        }
        return Content(outcome.Output, "application/json");
    }

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
