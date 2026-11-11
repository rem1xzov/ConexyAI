using System.Net;

namespace ConexyAI.Desktop;

/// <summary>
/// HTTP_PROXY: прокси для сервера и SignalR. Поддерживаются http://, https://, socks4://, socks5://.
/// Локальные адреса идут напрямую — иначе локальный тестовый сервер «пропадёт» за прокси.
/// </summary>
public static class NetProxy
{
    public static IWebProxy? FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Equals("off", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5" or "socks5h"))
            return null;
        return new WebProxy(uri)
        {
            BypassProxyOnLocal = true,
            BypassList = ["localhost", "127.0.0.1", "::1", "*.local"],
        };
    }

    public static bool IsValid(string? url) =>
        string.IsNullOrWhiteSpace(url)
        || url.Equals("off", StringComparison.OrdinalIgnoreCase)
        || FromUrl(url) is not null;

    /// <summary>Адрес без логина и пароля — для сообщений и журналов.</summary>
    public static string Describe(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}:{uri.Port}"
            : "напрямую";
}
