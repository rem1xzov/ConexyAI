using System.Collections.Concurrent;
using ConexyAI.Service.Web;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Playwright;

namespace ConexyAI.Service;

public class ConexyVisionService : IConexyVisionService, IAsyncDisposable
{
    // H9: добавлено 2026-09-24 — сколько байт одного файла рабочей области отдаётся браузеру и
    // сколько перенаправлений он может пройти за один запрос (каждый шаг проверяется заново).
    private const int MaxWorkspaceFileBytes = 20 * 1024 * 1024;
    private const int MaxRedirects = 5;

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly ILogger<ConexyVisionService> _logger;
    private readonly BrowserRequestPolicy _policy;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _lock = new(1, 1);
    // AGENT_TOOL_FAILURES: добавлено 2026-09-23 — Chromium в этом окружении собрать не удалось.
    // Флаг запоминается до перезапуска процесса, чтобы не пытаться и не платить за это на каждом
    // прогоне агента. Именно он выключает take_screenshot из списка инструментов.
    private bool _unavailable;

    public ConexyVisionService(
        ILogger<ConexyVisionService> logger,
        IWorkspacePathValidator pathValidator,
        PublicUrlGuard urlGuard)
    {
        _logger = logger;
        _policy = new BrowserRequestPolicy(urlGuard, pathValidator);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_browser != null) return true;
        if (_unavailable) return false;

        try
        {
            await EnsureInitializedAsync();
            return true;
        }
        catch (Exception ex)
        {
            _unavailable = true;
            _logger.LogWarning(
                ex,
                "Playwright Chromium is unavailable in this environment; take_screenshot will be hidden from the agent tool set.");
            return false;
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_browser != null) return;

        await _lock.WaitAsync();
        try
        {
            if (_browser == null)
            {
                _playwright = await Playwright.CreateAsync();

                try
                {
                    _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = true,
                        Args = new[] { "--no-sandbox", "--disable-setuid-sandbox" }
                    });
                }
                catch (Exception ex)
                {
                    // Не оставляем за собой полуинициализированный Playwright: при повторной попытке
                    // Playwright.CreateAsync() вызвался бы снова и оставил бы предыдущий объект висеть.
                    _playwright?.Dispose();
                    _playwright = null;

                    throw new InvalidOperationException(
                        "Playwright Chromium is not installed. Run 'npx playwright install chromium' before using take_screenshot.",
                        ex);
                }
            }
        }
        finally
        {
            // Always release the semaphore, even when initialization throws.
            _lock.Release();
        }
    }

    // H9: изменено 2026-09-24 — цель скриншота больше не резолвится от /app и не может быть любым
    // файлом хоста или внутренним адресом: см. BrowserRequestPolicy.
    public async Task<string> CaptureScreenshotBase64Async(
        Guid chatId,
        string targetUrlOrPath,
        int viewportWidth = 1280,
        int viewportHeight = 800,
        CancellationToken ct = default)
    {
        // Validated BEFORE the browser starts: a refused target must not cost a Chromium launch.
        var target = await _policy.ResolveTargetAsync(chatId, targetUrlOrPath, ct);

        await EnsureInitializedAsync();

        // A dedicated context per screenshot: context-level routes also cover popups, and service
        // workers (which bypass routing) are blocked.
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = viewportWidth > 0 ? viewportWidth : 1280,
                Height = viewportHeight > 0 ? viewportHeight : 800
            },
            ServiceWorkers = ServiceWorkerPolicy.Block,
            AcceptDownloads = false,
        });

        try
        {
            // One DNS verdict per host per screenshot: a page pulls dozens of resources from the same CDN.
            var verdicts = new ConcurrentDictionary<string, Task<BrowserRequestDecision>>(StringComparer.OrdinalIgnoreCase);
            await context.RouteAsync("**/*", route => HandleRouteAsync(chatId, route, verdicts, ct));
            // WebSockets are not needed for a screenshot and bypass HTTP routing: never connect them.
            await context.RouteWebSocketAsync("**/*", ws => _ = ws.CloseAsync());

            var page = await context.NewPageAsync();
            await page.GotoAsync(target.AbsoluteUri, new PageGotoOptions { Timeout = 15000, WaitUntil = WaitUntilState.NetworkIdle });

            var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Jpeg,
                Quality = 80
            });

            return Convert.ToBase64String(bytes);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    /// <summary>
    /// Every request of the screenshot page goes through here. Public http(s) is fetched by Playwright
    /// with redirects DISABLED and each hop re-checked — the browser's own redirect handling is not
    /// visible to routes ("the handler is only called for the first url"), so letting it follow
    /// redirects would reopen the SSRF hole this closes.
    /// </summary>
    private async Task HandleRouteAsync(
        Guid chatId,
        IRoute route,
        ConcurrentDictionary<string, Task<BrowserRequestDecision>> verdicts,
        CancellationToken ct)
    {
        try
        {
            var url = route.Request.Url;
            var decision = await DecideCachedAsync(chatId, url, verdicts, ct);

            switch (decision.Action)
            {
                case BrowserRequestAction.ServeWorkspaceFile:
                    await ServeWorkspaceFileAsync(route, decision.FilePath!);
                    return;

                case BrowserRequestAction.Continue:
                    await route.ContinueAsync();
                    return;

                case BrowserRequestAction.FetchPublic:
                    await FetchPublicAsync(chatId, route, url, verdicts, ct);
                    return;

                default:
                    _logger.LogInformation("take_screenshot: blocked a request ({Reason}).", decision.Reason);
                    await route.AbortAsync("blockedbyclient");
                    return;
            }
        }
        catch (Exception ex)
        {
            // The page may already be closed; a failed sub-resource must not break the screenshot.
            _logger.LogDebug(ex, "take_screenshot: request handling failed; aborting it.");
            try { await route.AbortAsync("failed"); } catch { /* route already handled or page closed */ }
        }
    }

    private async Task FetchPublicAsync(
        Guid chatId,
        IRoute route,
        string url,
        ConcurrentDictionary<string, Task<BrowserRequestDecision>> verdicts,
        CancellationToken ct)
    {
        var current = new Uri(url);
        var response = await route.FetchAsync(new RouteFetchOptions { MaxRedirects = 0, Timeout = 15000 });

        for (var hop = 0; response.Status is 301 or 302 or 303 or 307 or 308; hop++)
        {
            if (hop >= MaxRedirects || !response.Headers.TryGetValue("location", out var location))
                break;

            var next = new Uri(current, location);
            var decision = await DecideCachedAsync(chatId, next.AbsoluteUri, verdicts, ct);
            if (decision.Action != BrowserRequestAction.FetchPublic)
            {
                _logger.LogInformation("take_screenshot: blocked a redirect ({Reason}).", decision.Reason);
                await route.AbortAsync("blockedbyclient");
                return;
            }

            current = next;
            // 301/302/303 turn into GET (what browsers do); 307/308 keep the method and body.
            var keepMethod = response.Status is 307 or 308;
            response = await route.FetchAsync(new RouteFetchOptions
            {
                Url = next.AbsoluteUri,
                Method = keepMethod ? null : "GET",
                PostData = keepMethod ? null : Array.Empty<byte>(),
                MaxRedirects = 0,
                Timeout = 15000,
            });
        }

        await route.FulfillAsync(new RouteFulfillOptions { Response = response });
    }

    private static async Task ServeWorkspaceFileAsync(IRoute route, string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length > MaxWorkspaceFileBytes)
        {
            await route.FulfillAsync(new RouteFulfillOptions { Status = 404, ContentType = "text/plain", Body = "Not found" });
            return;
        }

        if (!ContentTypes.TryGetContentType(fullPath, out var contentType))
            contentType = "application/octet-stream";

        await route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = contentType,
            BodyBytes = await File.ReadAllBytesAsync(fullPath),
        });
    }

    private Task<BrowserRequestDecision> DecideCachedAsync(
        Guid chatId,
        string url,
        ConcurrentDictionary<string, Task<BrowserRequestDecision>> verdicts,
        CancellationToken ct)
    {
        // Workspace files are decided per path (the jail check is per file), public hosts per host.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.Equals(uri.Host, BrowserRequestPolicy.WorkspaceHost, StringComparison.OrdinalIgnoreCase))
        {
            return _policy.DecideAsync(chatId, url, ct);
        }

        return verdicts.GetOrAdd(uri.GetLeftPart(UriPartial.Authority), _ => _policy.DecideAsync(chatId, url, ct));
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null) await _browser.DisposeAsync();
        _playwright?.Dispose();
        _lock.Dispose();
    }
}
