using System.Collections.Concurrent;
using System.Text;
using ConexyAI.Service.Web;
using Microsoft.Playwright;

namespace ConexyAI.Service;

// BROWSER_AUTOMATION: добавлено 2026-10-01.
//
// DOM-автоматизация для агента: открыть страницу, кликнуть, ввести текст, прочитать содержимое.
// Модель работает селекторами и текстом, а не координатами — это дёшево и детерминированно.
//
// Безопасность ровно та же, что у take_screenshot (общий BrowserRouteHandler + BrowserRequestPolicy):
// только публичные http(s) и файлы рабочей области чата, редиректы перепроверяются, WebSockets и
// скачивания запрещены. Действия не выходят за пределы страницы: браузер не несёт чужих куков и не
// авторизуется нигде от имени пользователя.
//
// Сессия живёт на чат: один контекст+страница, чтобы клик и следующий за ним extract видели одну и
// ту же страницу. По простою сессия закрывается (SessionTtl).

/// <summary>Результат одного браузерного шага, который видит модель.</summary>
public sealed record BrowserStepResult(bool Success, string Message, string? ScreenshotBase64 = null);

public interface IConexyBrowserService
{
    Task<BrowserStepResult> OpenAsync(Guid chatId, string target, CancellationToken ct = default);
    Task<BrowserStepResult> ClickAsync(Guid chatId, string? selector, string? text, CancellationToken ct = default);
    Task<BrowserStepResult> TypeAsync(Guid chatId, string selector, string text, bool submit, CancellationToken ct = default);
    Task<BrowserStepResult> PressAsync(Guid chatId, string key, CancellationToken ct = default);
    Task<BrowserStepResult> ScrollAsync(Guid chatId, string direction, int amount, CancellationToken ct = default);
    Task<BrowserStepResult> ExtractAsync(Guid chatId, int maxChars, CancellationToken ct = default);
    Task<BrowserStepResult> ScreenshotAsync(Guid chatId, CancellationToken ct = default);
    Task CloseAsync(Guid chatId, CancellationToken ct = default);
}

public sealed class ConexyBrowserService : IConexyBrowserService
{
    private const int NavigateTimeoutMs = 15_000;
    private const int ActionTimeoutMs = 5_000;
    private const int DefaultExtractChars = 8_000;
    private const int MaxExtractChars = 40_000;
    private const int MaxInteractiveElements = 60;
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(10);

    // Список кликабельных/вводимых элементов: помогает модели выбрать селектор, не гадая.
    private const string InteractiveElementsScript = """
        () => {
          const out = [];
          const els = document.querySelectorAll('a,button,input,select,textarea,[role="button"],[role="link"],[onclick]');
          for (const el of els) {
            if (out.length >= 60) break;
            const r = el.getBoundingClientRect();
            if (r.width === 0 && r.height === 0) continue;
            const tag = el.tagName.toLowerCase();
            const label = (el.innerText || el.value || el.getAttribute('aria-label') || el.getAttribute('placeholder') || '')
              .trim().replace(/\s+/g, ' ').slice(0, 60);
            let sel;
            if (el.id) sel = '#' + CSS.escape(el.id);
            else if (el.getAttribute('name')) sel = tag + '[name="' + el.getAttribute('name') + '"]';
            else if (tag === 'a' && el.getAttribute('href')) sel = 'a[href="' + el.getAttribute('href') + '"]';
            else sel = tag;
            out.push('- ' + sel + (label ? ' — "' + label + '"' : ''));
          }
          return out.join('\n');
        }
        """;

    private readonly ILogger<ConexyBrowserService> _logger;
    private readonly IChromiumHost _chromium;
    private readonly BrowserRequestPolicy _policy;
    private readonly BrowserRouteHandler _routes;
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public ConexyBrowserService(
        ILogger<ConexyBrowserService> logger,
        IChromiumHost chromium,
        IWorkspacePathValidator pathValidator,
        PublicUrlGuard urlGuard)
    {
        _logger = logger;
        _chromium = chromium;
        _policy = new BrowserRequestPolicy(urlGuard, pathValidator);
        _routes = new BrowserRouteHandler(_policy, logger);
    }

    private sealed class Session
    {
        public required IBrowserContext Context { get; init; }
        public required IPage Page { get; init; }
        public required ConcurrentDictionary<string, Task<BrowserRequestDecision>> Verdicts { get; init; }
        public required SemaphoreSlim Gate { get; init; }
        public DateTime LastUsedAt { get; set; }
    }

    public async Task<BrowserStepResult> OpenAsync(Guid chatId, string target, CancellationToken ct = default)
    {
        Uri uri;
        try
        {
            // Резолвим ДО создания сессии: запрещённый адрес не должен стоить запуска страницы.
            uri = await _policy.ResolveTargetAsync(chatId, target, ct);
        }
        catch (ScreenshotTargetException ex)
        {
            return new BrowserStepResult(false, ex.Message);
        }

        EvictExpired();
        var session = await GetOrCreateSessionAsync(chatId, ct);
        await session.Gate.WaitAsync(ct);
        try
        {
            session.LastUsedAt = DateTime.UtcNow;
            var response = await session.Page.GotoAsync(uri.AbsoluteUri, new PageGotoOptions
            {
                Timeout = NavigateTimeoutMs,
                WaitUntil = WaitUntilState.DOMContentLoaded,
            });
            var status = response is null ? string.Empty : $" (HTTP {response.Status})";
            return await DescribeAsync(session.Page, $"Открыл {uri.AbsoluteUri}{status}", ct);
        }
        catch (PlaywrightException ex)
        {
            return new BrowserStepResult(false, $"Не удалось открыть страницу: {ex.Message}");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public Task<BrowserStepResult> ClickAsync(Guid chatId, string? selector, string? text, CancellationToken ct = default) =>
        WithSessionAsync(chatId, async session =>
        {
            ILocator locator;
            if (!string.IsNullOrWhiteSpace(selector))
                locator = session.Page.Locator(selector).First;
            else if (!string.IsNullOrWhiteSpace(text))
                locator = session.Page.GetByText(text, new PageGetByTextOptions { Exact = false }).First;
            else
                return new BrowserStepResult(false, "browser_click требует 'selector' или 'text'.");

            try
            {
                await locator.WaitForAsync(new LocatorWaitForOptions { Timeout = ActionTimeoutMs, State = WaitForSelectorState.Visible });
                await locator.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs });
            }
            catch (PlaywrightException ex)
            {
                return new BrowserStepResult(false,
                    $"Клик не удался: {ex.Message}. Уточни селектор через browser_extract.");
            }

            await SettleAsync(session.Page);
            return await DescribeAsync(session.Page, "Кликнул.", ct);
        }, ct);

    public Task<BrowserStepResult> TypeAsync(Guid chatId, string selector, string text, bool submit, CancellationToken ct = default) =>
        WithSessionAsync(chatId, async session =>
        {
            if (string.IsNullOrWhiteSpace(selector))
                return new BrowserStepResult(false, "browser_type требует 'selector'.");

            try
            {
                var locator = session.Page.Locator(selector).First;
                await locator.WaitForAsync(new LocatorWaitForOptions { Timeout = ActionTimeoutMs, State = WaitForSelectorState.Visible });
                await locator.FillAsync(text ?? string.Empty, new LocatorFillOptions { Timeout = ActionTimeoutMs });
                if (submit)
                    await locator.PressAsync("Enter", new LocatorPressOptions { Timeout = ActionTimeoutMs });
            }
            catch (PlaywrightException ex)
            {
                return new BrowserStepResult(false, $"Ввод не удался: {ex.Message}");
            }

            await SettleAsync(session.Page);
            return await DescribeAsync(session.Page, submit ? "Ввёл текст и отправил форму." : "Ввёл текст.", ct);
        }, ct);

    public Task<BrowserStepResult> PressAsync(Guid chatId, string key, CancellationToken ct = default) =>
        WithSessionAsync(chatId, async session =>
        {
            if (string.IsNullOrWhiteSpace(key))
                return new BrowserStepResult(false, "browser_press требует 'key' (например 'Enter' или 'Escape').");

            try
            {
                await session.Page.Keyboard.PressAsync(key);
            }
            catch (PlaywrightException ex)
            {
                return new BrowserStepResult(false, $"Нажатие не удалось: {ex.Message}");
            }

            await SettleAsync(session.Page);
            return await DescribeAsync(session.Page, $"Нажал {key}.", ct);
        }, ct);

    public Task<BrowserStepResult> ScrollAsync(Guid chatId, string direction, int amount, CancellationToken ct = default) =>
        WithSessionAsync(chatId, async session =>
        {
            var delta = amount <= 0 ? 600 : Math.Min(amount, 5000);
            var up = string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase);
            try
            {
                await session.Page.Mouse.WheelAsync(0, up ? -delta : delta);
            }
            catch (PlaywrightException ex)
            {
                return new BrowserStepResult(false, $"Прокрутка не удалась: {ex.Message}");
            }

            return await DescribeAsync(session.Page, up ? "Прокрутил вверх." : "Прокрутил вниз.", ct);
        }, ct);

    public Task<BrowserStepResult> ExtractAsync(Guid chatId, int maxChars, CancellationToken ct = default) =>
        WithSessionAsync(chatId, async session =>
        {
            var limit = Math.Clamp(maxChars <= 0 ? DefaultExtractChars : maxChars, 500, MaxExtractChars);
            var text = await session.Page.Locator("body").InnerTextAsync();
            text = text.Replace("\r\n", "\n").Trim();
            if (text.Length > limit)
                text = text[..limit] + "\n… (текст обрезан)";

            var sb = new StringBuilder();
            sb.AppendLine($"URL: {session.Page.Url}");
            sb.AppendLine($"Заголовок: {await session.Page.TitleAsync()}");
            sb.AppendLine();
            sb.AppendLine(text);
            var elements = await SafeElementsAsync(session.Page);
            if (!string.IsNullOrWhiteSpace(elements))
            {
                sb.AppendLine();
                sb.AppendLine("Интерактивные элементы (селектор — что это):");
                sb.AppendLine(elements);
            }

            return new BrowserStepResult(true, sb.ToString().TrimEnd());
        }, ct);

    public Task<BrowserStepResult> ScreenshotAsync(Guid chatId, CancellationToken ct = default) =>
        WithSessionAsync(chatId, async session =>
        {
            var bytes = await session.Page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Jpeg,
                Quality = 80,
            });
            return new BrowserStepResult(true, "Скриншот текущей страницы.", Convert.ToBase64String(bytes));
        }, ct);

    public async Task CloseAsync(Guid chatId, CancellationToken ct = default)
    {
        if (_sessions.TryRemove(chatId, out var session))
            await SafeCloseAsync(session);
    }

    private async Task<BrowserStepResult> WithSessionAsync(
        Guid chatId,
        Func<Session, Task<BrowserStepResult>> action,
        CancellationToken ct)
    {
        EvictExpired();

        if (!_sessions.TryGetValue(chatId, out var session) || session.Page.IsClosed)
            return new BrowserStepResult(false, "Страница не открыта. Сначала вызови browser_open с адресом страницы.");

        await session.Gate.WaitAsync(ct);
        try
        {
            session.LastUsedAt = DateTime.UtcNow;
            return await action(session);
        }
        catch (PlaywrightException ex)
        {
            _logger.LogWarning(ex, "Browser automation step failed for chat {ChatId}.", chatId);
            return new BrowserStepResult(false, $"Браузерная операция не удалась: {ex.Message}");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<BrowserStepResult> DescribeAsync(IPage page, string prefix, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(prefix);
        sb.AppendLine($"URL: {page.Url}");
        sb.AppendLine($"Заголовок: {await page.TitleAsync()}");
        var elements = await SafeElementsAsync(page);
        if (!string.IsNullOrWhiteSpace(elements))
        {
            sb.AppendLine("Интерактивные элементы (селектор — что это):");
            sb.AppendLine(elements);
        }

        return new BrowserStepResult(true, sb.ToString().TrimEnd());
    }

    private static async Task<string> SafeElementsAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string>(InteractiveElementsScript) ?? string.Empty;
        }
        catch (PlaywrightException)
        {
            return string.Empty;
        }
    }

    /// <summary>Короткая пауза, чтобы навигация после клика/отправки успела начаться.</summary>
    private static async Task SettleAsync(IPage page)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 3000 });
        }
        catch (PlaywrightException)
        {
            // Показ оставшегося DOM — нормальное состояние для SPA; не мешаем шагу завершиться.
        }
    }

    private async Task<Session> GetOrCreateSessionAsync(Guid chatId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(chatId, out var existing) && !existing.Page.IsClosed)
            return existing;

        var browser = await _chromium.GetBrowserAsync(ct);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            ServiceWorkers = ServiceWorkerPolicy.Block,
            AcceptDownloads = false,
        });

        var verdicts = new ConcurrentDictionary<string, Task<BrowserRequestDecision>>(StringComparer.OrdinalIgnoreCase);
        await context.RouteAsync("**/*", route => _routes.HandleAsync(chatId, route, verdicts, ct));
        await context.RouteWebSocketAsync("**/*", ws => _ = ws.CloseAsync());

        var page = await context.NewPageAsync();
        var session = new Session
        {
            Context = context,
            Page = page,
            Verdicts = verdicts,
            Gate = new SemaphoreSlim(1, 1),
            LastUsedAt = DateTime.UtcNow,
        };

        if (_sessions.TryRemove(chatId, out var stale))
            await SafeCloseAsync(stale);

        _sessions[chatId] = session;
        _logger.LogInformation("Browser session opened for chat {ChatId}.", chatId);
        return session;
    }

    /// <summary>Закрывает сессии, простоявшие дольше TTL. Занятые шагом не трогаем.</summary>
    private void EvictExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var (chatId, session) in _sessions)
        {
            if (now - session.LastUsedAt <= SessionTtl) continue;
            // Gate занят (CurrentCount == 0) — шаг выполняется прямо сейчас, подождём следующего раза.
            if (session.Gate.CurrentCount == 0) continue;
            if (_sessions.TryRemove(chatId, out var removed))
                _ = SafeCloseAsync(removed);
        }
    }

    private async Task SafeCloseAsync(Session session)
    {
        try
        {
            await session.Context.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Closing a browser session failed; ignoring.");
        }
        finally
        {
            session.Gate.Dispose();
        }
    }
}
