using Microsoft.Playwright;

namespace ConexyAI.Service;

// BROWSER_AUTOMATION: добавлено 2026-10-01.
//
// Один headless Chromium на процесс, общий для скриншотов (take_screenshot) и для браузерной
// автоматизации (browser_*). Раньше браузер жил внутри ConexyVisionService; вынесен сюда, чтобы
// второй сервис не поднимал ВТОРОЙ Chromium — это лишние сотни мегабайт и второй набор процессов.
public interface IChromiumHost
{
    /// <summary>
    /// Whether headless Chromium can actually be launched here. A failed probe is remembered until the
    /// process restarts, so the agent's tool list can be trimmed without paying for the launch again.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>The shared browser. Throws when Chromium is not installed.</summary>
    Task<IBrowser> GetBrowserAsync(CancellationToken ct = default);
}

public sealed class ChromiumHost : IChromiumHost, IAsyncDisposable
{
    private readonly ILogger<ChromiumHost> _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _unavailable;

    public ChromiumHost(ILogger<ChromiumHost> logger)
    {
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_browser != null) return true;
        if (_unavailable) return false;

        try
        {
            await GetBrowserAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _unavailable = true;
            _logger.LogWarning(
                ex,
                "Playwright Chromium is unavailable in this environment; screenshot and browser tools will be hidden from the agent.");
            return false;
        }
    }

    public async Task<IBrowser> GetBrowserAsync(CancellationToken ct = default)
    {
        if (_browser != null) return _browser;

        await _lock.WaitAsync(ct);
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
                        // BROWSER_AUTOMATION: если в образ поставлен системный Chromium, указываем его
                        // путь явно — тогда не нужен встроенный браузер Playwright. Пусто — Playwright
                        // берёт свой.
                        ExecutablePath = ResolveExecutablePath(),
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
                        "Playwright Chromium is not installed. Run 'npx playwright install chromium' before using browser tools.",
                        ex);
                }
            }

            return _browser;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null) await _browser.DisposeAsync();
        _playwright?.Dispose();
        _lock.Dispose();
    }

    /// <summary>Путь к системному Chromium, если он задан окружением; иначе null (встроенный Playwright).</summary>
    private static string? ResolveExecutablePath()
    {
        var path = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH");
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}
