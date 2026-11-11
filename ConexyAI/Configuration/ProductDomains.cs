namespace ConexyAI.Configuration;

// DOMAIN_SSO: добавлено 2026-11-11 — у продукта два домена (conexyai.ru и conexy.ru), и человек
// должен видеть один и тот же аккаунт на обоих. Список доменов живёт в одном месте: он нужен и
// CORS (Program.cs), и переносу сессии (AuthController). Разъехавшись, они дали бы «на conexy.ru
// сайт открывается, а вход не работает».
/// <summary>
/// The domains this product is served from. One list for CORS and for the cross-domain session
/// handoff; localhost is not part of it (development runs same-origin on the Vite server).
/// </summary>
public static class ProductDomains
{
    /// <summary>Apex and www of both domains, without a scheme.</summary>
    public static readonly IReadOnlyList<string> Hosts = new[]
    {
        "conexyai.ru", "www.conexyai.ru",
        "conexy.ru", "www.conexy.ru",
    };

    /// <summary>True for a bare host name we serve the app from.</summary>
    public static bool IsKnownHost(string? host) =>
        host is not null && Hosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    /// <summary>True for an <c>Origin</c> header value of one of our domains (always https).</summary>
    public static bool IsKnownOrigin(string? origin)
    {
        if (origin is null) return false;
        foreach (var host in Hosts)
        {
            if (string.Equals(origin, "https://" + host, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// A same-site path we may redirect to after the session handoff. Anything that could leave the
    /// site — an absolute URL, a protocol-relative <c>//host</c>, a backslash-smuggled <c>/\host</c>
    /// — collapses to the site root, so <c>next</c> can never become an open redirect.
    /// </summary>
    public static string SafeLocalPath(string? path) =>
        path is { Length: > 1 } && path[0] == '/' && path[1] is not ('/' or '\\') ? path : "/";
}
