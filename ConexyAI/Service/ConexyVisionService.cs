using System.Collections.Concurrent;
using ConexyAI.Service.Web;
using Microsoft.Playwright;

namespace ConexyAI.Service;

public class ConexyVisionService : IConexyVisionService
{
    private readonly IChromiumHost _chromium;
    private readonly BrowserRequestPolicy _policy;
    private readonly BrowserRouteHandler _routes;

    public ConexyVisionService(
        ILogger<ConexyVisionService> logger,
        IChromiumHost chromium,
        IWorkspacePathValidator pathValidator,
        PublicUrlGuard urlGuard)
    {
        _chromium = chromium;
        _policy = new BrowserRequestPolicy(urlGuard, pathValidator);
        _routes = new BrowserRouteHandler(_policy, logger);
    }

    // BROWSER_AUTOMATION: жизненный цикл браузера переехал в общий IChromiumHost (см. ChromiumHost) —
    // чтобы браузерная автоматизация не поднимала второй Chromium.
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => _chromium.IsAvailableAsync(ct);

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

        var browser = await _chromium.GetBrowserAsync(ct);

        // A dedicated context per screenshot: context-level routes also cover popups, and service
        // workers (which bypass routing) are blocked.
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
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
            await context.RouteAsync("**/*", route => _routes.HandleAsync(chatId, route, verdicts, ct));
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
}
