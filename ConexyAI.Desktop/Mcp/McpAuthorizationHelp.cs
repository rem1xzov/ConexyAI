using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ConexyAI.Desktop.Mcp;

/// <summary>
/// MCP_AUTH_BROWSER: удалённым MCP (Figma, Slack, Notion …) нужен вход в браузере. Обычно его открывает
/// сам мост mcp-remote, но когда автозапуск не сработал, пользователь видел только «MCP-сервер завершился».
/// Здесь ссылка достаётся из вывода сервера и открывается в браузере, а в консоль идёт понятная подсказка.
/// </summary>
public static partial class McpAuthorizationHelp
{
    [GeneratedRegex(@"https?://[^\s""'<>()\[\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    private static readonly string[] AuthMarkers =
    [
        "authorize", "authorization", "oauth", "unauthorized", "401", "403",
        "sign in", "log in", "login", "consent", "access token", "bearer",
        // ru: сторонние серверы пишут и по-русски
        "авториз", "войдите", "вход",
    ];

    /// <summary>Нужен ли вход: по тексту ошибки и по наличию ссылки.</summary>
    public static bool LooksLikeAuthorizationRequired(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        return AuthMarkers.Any(marker => lower.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>Первая ссылка из вывода сервера, похожая на страницу входа.</summary>
    public static string? FindAuthorizationUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match match in UrlPattern().Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ':', ')');
            var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
            // Служебные адреса моста (колбэк на 127.0.0.1) пользователю бесполезны.
            if (host is "127.0.0.1" or "localhost") continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                uri.Scheme is not ("http" or "https"))
                continue;
            return url;
        }
        return null;
    }

    /// <summary>
    /// Открывает браузер и печатает подсказку. Только в интерактивном терминале: в демоне и в тестах
    /// (ввод перенаправлен) окно браузера никого не ждёт.
    /// </summary>
    public static void Offer(string serverName, string? text)
    {
        if (!LooksLikeAuthorizationRequired(text)) return;
        var url = FindAuthorizationUrl(text);

        if (url is null)
        {
            Ui.Info($"  MCP «{serverName}» требует авторизации. Перезапустите его командой /mcp reload — окно входа откроется заново.");
            return;
        }

        Ui.Info($"  MCP «{serverName}» требует авторизации. Открываю браузер: {url}");
        if (Console.IsInputRedirected) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Ui.Info($"  Не удалось открыть браузер ({ex.Message}). Откройте ссылку вручную.");
        }
    }
}
