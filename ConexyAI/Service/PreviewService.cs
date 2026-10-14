using System.Collections.Concurrent;
using System.Security.Cryptography;
using ConexyAI.Configuration;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// PREVIEW: start_service + preview-gateway. Агент запускает сервер (dev-server/статику) в отдельном
// долгоживущем контейнере; пользователь открывает его на {id}.{Domain} по токену, а браузер агента видит
// тот же сервис как свой http://localhost:{port}.
public sealed record PreviewInfo(Guid ChatId, string Id, string Token, string Container, int Port, string Command, DateTime StartedAt)
{
    public DateTime LastTouchedAt { get; set; } = DateTime.UtcNow;
}

public sealed record PreviewStartResult(bool Success, string Message, PreviewInfo? Info = null);

public interface IPreviewService
{
    Task<PreviewStartResult> StartAsync(Guid chatId, string command, int port, CancellationToken ct = default);
    Task<bool> StopAsync(Guid chatId);
    Task<string> LogsAsync(Guid chatId, int lines, CancellationToken ct = default);
    PreviewInfo? GetForChat(Guid chatId);
    PreviewInfo? GetBySubdomain(string id);
    string PublicUrl(PreviewInfo info);

    /// <summary>Internal container URL for <c>http://localhost:{port}/…</c> when the chat runs that service; else null.</summary>
    Uri? MapLoopback(Guid chatId, Uri uri);

    /// <summary>Subdomain id from a Host header, or null when the host is not a preview host.</summary>
    string? ParseHost(string host);
}

public sealed class PreviewService : IPreviewService
{
    private readonly IOptions<PreviewOptions> _options;
    private readonly IDockerServiceRunner _docker;
    private readonly IConexyWorkspaceService _workspace;
    private readonly ISandboxSessionStore _sessions;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<PreviewService> _logger;
    private readonly ConcurrentDictionary<Guid, PreviewInfo> _byChat = new();
    private readonly ConcurrentDictionary<string, PreviewInfo> _bySub = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PreviewService(
        IOptions<PreviewOptions> options,
        IDockerServiceRunner docker,
        IConexyWorkspaceService workspace,
        ISandboxSessionStore sessions,
        IHttpClientFactory http,
        ILogger<PreviewService> logger)
    {
        _options = options;
        _docker = docker;
        _workspace = workspace;
        _sessions = sessions;
        _http = http;
        _logger = logger;
    }

    public async Task<PreviewStartResult> StartAsync(Guid chatId, string command, int port, CancellationToken ct = default)
    {
        var opt = _options.Value;
        if (!opt.Enabled)
            return new PreviewStartResult(false, "Превью отключено на сервере.");
        if (string.IsNullOrWhiteSpace(command))
            return new PreviewStartResult(false, "start_service requires 'command'.");
        if (port is < 1024 or > 65535)
            return new PreviewStartResult(false, "Порт должен быть в диапазоне 1024–65535.");

        var workspacePath = _workspace.GetTaskWorkspacePathIfExists(chatId);
        if (workspacePath is null)
            return new PreviewStartResult(false, "Рабочая область не найдена: сначала создай файлы проекта.");
        workspacePath = _workspace.ValidateWorkspacePath(workspacePath);

        await _lock.WaitAsync(ct);
        try
        {
            await ReapExpiredAsync();
            // Один сервис на чат: повторный запуск заменяет предыдущий (тот же поддомен/токен сохраняются).
            _byChat.TryGetValue(chatId, out var previous);
            if (previous is not null)
                await _docker.StopServiceAsync(previous.Container);

            var id = previous?.Id ?? RandomHex(5);
            var token = previous?.Token ?? RandomHex(16);
            var container = $"conexy-svc-{Guid.NewGuid():N}";

            var started = await _docker.StartServiceAsync(
                command, workspacePath, _sessions.GetOrCreateStatePath(chatId), container, opt.Network, ct);
            if (!started.Ok)
                return new PreviewStartResult(false, $"Не удалось запустить сервис: {started.Output}");

            var info = new PreviewInfo(chatId, id, token, container, port, command, DateTime.UtcNow);
            _byChat[chatId] = info;
            _bySub[id] = info;
            _logger.LogInformation("Preview service started for chat {ChatId} on port {Port}.", chatId, port);

            var ready = await WaitForPortAsync(info, TimeSpan.FromSeconds(opt.StartupWaitSeconds), ct);
            var logs = await _docker.TailLogsAsync(container, 30, ct);
            if (!ready)
            {
                var running = await _docker.IsRunningAsync(container, ct);
                var why = running
                    ? $"Порт {port} не ответил за {opt.StartupWaitSeconds} с (сервер должен слушать 0.0.0.0:{port})."
                    : "Процесс завершился сразу после запуска.";
                return new PreviewStartResult(false, $"{why}\nЛоги:\n{logs}", info);
            }

            return new PreviewStartResult(true,
                $"Сервис запущен. Браузер агента видит его как http://localhost:{port}/ (browser_open). " +
                $"Пользователь открывает вкладку «Превью».\nЛоги:\n{logs}", info);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> StopAsync(Guid chatId)
    {
        if (!_byChat.TryRemove(chatId, out var info)) return false;
        _bySub.TryRemove(info.Id, out _);
        await _docker.StopServiceAsync(info.Container);
        return true;
    }

    public async Task<string> LogsAsync(Guid chatId, int lines, CancellationToken ct = default) =>
        _byChat.TryGetValue(chatId, out var info) ? await _docker.TailLogsAsync(info.Container, lines, ct) : string.Empty;

    public PreviewInfo? GetForChat(Guid chatId) => Touch(_byChat.GetValueOrDefault(chatId));

    public PreviewInfo? GetBySubdomain(string id) => Touch(_bySub.GetValueOrDefault(id));

    public string PublicUrl(PreviewInfo info) => $"{_options.Value.Scheme}://{info.Id}.{_options.Value.Domain}/";

    public Uri? MapLoopback(Guid chatId, Uri uri)
    {
        if (!_byChat.TryGetValue(chatId, out var info)) return null;
        if (uri.Scheme != "http" || uri.Port != info.Port) return null;
        if (!(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host is "127.0.0.1" or "[::1]" or "::1"))
            return null;
        Touch(info);
        return new Uri($"http://{info.Container}:{info.Port}{uri.PathAndQuery}");
    }

    public string? ParseHost(string host)
    {
        var domain = _options.Value.Domain;
        if (!_options.Value.Enabled || string.IsNullOrEmpty(domain)) return null;
        var h = host.Split(':')[0];
        var suffix = "." + domain;
        if (!h.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        var sub = h[..^suffix.Length];
        return sub.Length > 0 && !sub.Contains('.') ? sub : null;
    }

    private static PreviewInfo? Touch(PreviewInfo? info)
    {
        if (info is not null) info.LastTouchedAt = DateTime.UtcNow;
        return info;
    }

    private async Task ReapExpiredAsync()
    {
        var limit = TimeSpan.FromMinutes(_options.Value.MaxLifetimeMinutes);
        foreach (var info in _byChat.Values.Where(i => DateTime.UtcNow - i.LastTouchedAt > limit).ToList())
            await StopAsync(info.ChatId);
    }

    private async Task<bool> WaitForPortAsync(PreviewInfo info, TimeSpan timeout, CancellationToken ct)
    {
        var client = _http.CreateClient("preview");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                using var response = await client.GetAsync($"http://{info.Container}:{info.Port}/", HttpCompletionOption.ResponseHeadersRead, cts.Token);
                return true;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // ещё не слушает
            }

            if (!await _docker.IsRunningAsync(info.Container, ct)) return false;
            await Task.Delay(500, ct);
        }

        return false;
    }

    private static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
}
