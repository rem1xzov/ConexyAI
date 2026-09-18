using Microsoft.Playwright;

namespace ConexyAI.Service;

public class ConexyVisionService : IConexyVisionService, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _lock = new(1, 1);

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

    public async Task<string> CaptureScreenshotBase64Async(
        string targetUrlOrPath,
        int viewportWidth = 1280,
        int viewportHeight = 800,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        var page = await _browser!.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = viewportWidth > 0 ? viewportWidth : 1280,
                Height = viewportHeight > 0 ? viewportHeight : 800
            }
        });

        try
        {
            var uri = targetUrlOrPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? targetUrlOrPath
                : new Uri(Path.GetFullPath(targetUrlOrPath)).AbsoluteUri;

            await page.GotoAsync(uri, new PageGotoOptions { Timeout = 15000, WaitUntil = WaitUntilState.NetworkIdle });
            
            var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Jpeg,
                Quality = 80
            });

            return Convert.ToBase64String(bytes);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null) await _browser.DisposeAsync();
        _playwright?.Dispose();
        _lock.Dispose();
    }
}