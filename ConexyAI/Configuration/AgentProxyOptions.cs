using System.Net;

namespace ConexyAI.Configuration;

// AGENT_PROXY: добавлено 2026-11-03 — исходящий прокси для интернет-инструментов агента (чтение страниц
// и браузер), чтобы открывались ресурсы, заблокированные у хостинга. Адрес с логином/паролем задаётся
// ТОЛЬКО через окружение (AGENT_PROXY_URL), в репозитории его нет. Пусто — прокси выключен.
public sealed class AgentProxyOptions
{
    public const string SectionName = "AgentProxy";

    /// <summary>http://user:pass@host:port (рекомендуется — его понимает и Chromium) или socks5://…</summary>
    public string Url { get; set; } = string.Empty;

    public bool IsConfigured => TryParse(out _);

    public bool TryParse(out Uri uri) =>
        Uri.TryCreate((Url ?? string.Empty).Trim(), UriKind.Absolute, out uri!) &&
        uri.Scheme is "http" or "https" or "socks5" or "socks5h" or "socks4" &&
        !string.IsNullOrEmpty(uri.Host) && uri.Port > 0;

    /// <summary>Прокси для HttpClient (SocketsHttpHandler поддерживает http и socks).</summary>
    public IWebProxy? CreateWebProxy()
    {
        if (!TryParse(out var uri)) return null;
        var scheme = uri.Scheme == "socks5h" ? "socks5" : uri.Scheme;
        var proxy = new WebProxy(new Uri($"{scheme}://{uri.Host}:{uri.Port}"));
        if (Credentials(uri) is { } creds) proxy.Credentials = creds;
        return proxy;
    }

    public static NetworkCredential? Credentials(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.UserInfo)) return null;
        var parts = uri.UserInfo.Split(':', 2);
        return new NetworkCredential(
            Uri.UnescapeDataString(parts[0]),
            parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
    }

    /// <summary>Адрес без логина/пароля — для логов.</summary>
    public string Describe() => TryParse(out var uri) ? $"{uri.Scheme}://{uri.Host}:{uri.Port}" : "off";
}
