using System.Collections.Concurrent;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Playwright;

namespace ConexyAI.Service.Web;

// BROWSER_AUTOMATION: добавлено 2026-10-01 — маршрутизация запросов браузера вынесена из
// ConexyVisionService, чтобы её использовали ОБА потребителя: take_screenshot и browser_*.
// Логика и границы безопасности не изменились: рабочая область сервится со своего хоста, публичные
// http(s) идут через PublicUrlGuard, редиректы перепроверяются на каждом шаге, остальное блокируется.
public sealed class BrowserRouteHandler
{
    // H9: сколько байт одного файла рабочей области отдаётся браузеру и сколько перенаправлений он
    // может пройти за один запрос (каждый шаг проверяется заново).
    private const int MaxWorkspaceFileBytes = 20 * 1024 * 1024;
    private const int MaxRedirects = 5;

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly BrowserRequestPolicy _policy;
    private readonly ILogger _logger;

    public BrowserRouteHandler(BrowserRequestPolicy policy, ILogger logger)
    {
        _policy = policy;
        _logger = logger;
    }

    /// <summary>
    /// Every request of a page goes through here. Public http(s) is fetched by Playwright with redirects
    /// DISABLED and each hop re-checked — the browser's own redirect handling is not visible to routes
    /// ("the handler is only called for the first url"), so letting it follow redirects would reopen the
    /// SSRF hole this closes.
    /// </summary>
    public async Task HandleAsync(
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
                    _logger.LogInformation("browser: blocked a request ({Reason}).", decision.Reason);
                    await route.AbortAsync("blockedbyclient");
                    return;
            }
        }
        catch (Exception ex)
        {
            // The page may already be closed; a failed sub-resource must not break the caller.
            _logger.LogDebug(ex, "browser: request handling failed; aborting it.");
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
                _logger.LogInformation("browser: blocked a redirect ({Reason}).", decision.Reason);
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
}
