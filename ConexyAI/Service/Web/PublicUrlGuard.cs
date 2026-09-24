using System.Net;
using System.Net.Sockets;

namespace ConexyAI.Service.Web;

// AGENT_WEB_TOOLS: добавлено 2026-09-24 — единая SSRF-проверка для всего, что ходит в сеть по URL,
// который выбрала модель: fetch_web_page и take_screenshot. Бэкенд живёт в одной docker-сети с
// Postgres, docker-socket-proxy и метаданными облака (169.254.169.254), поэтому «скачай этот URL» без
// такой проверки — это чтение секретов и управление Docker изнутри.

/// <summary>DNS seam: production uses the system resolver, tests substitute a fixed table.</summary>
public interface IHostAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>System DNS (getaddrinfo) resolver.</summary>
public sealed class DnsHostAddressResolver : IHostAddressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>Outcome of a <see cref="PublicUrlGuard"/> check.</summary>
/// <param name="Allowed">True when every address the host resolves to is public.</param>
/// <param name="Error">Human-readable reason when <paramref name="Allowed"/> is false.</param>
/// <param name="Addresses">The validated addresses; the caller must connect to one of THESE (pinning).</param>
public sealed record UrlCheckResult(bool Allowed, string? Error, IReadOnlyList<IPAddress> Addresses)
{
    public static UrlCheckResult Deny(string error) => new(false, error, Array.Empty<IPAddress>());
    public static UrlCheckResult Allow(IReadOnlyList<IPAddress> addresses) => new(true, null, addresses);
}

/// <summary>Thrown from a connect callback when a host fails the public-address check at connect time.</summary>
public sealed class PublicUrlRejectedException : Exception
{
    public PublicUrlRejectedException(string message) : base(message) { }
}

/// <summary>
/// Decides whether a URL chosen by the model may be fetched from the backend: only http/https to
/// hosts whose EVERY resolved address is a public unicast address. Loopback, RFC 1918, link-local
/// (cloud metadata), CGNAT, multicast, reserved and documentation ranges are refused, in IPv4, IPv6
/// and IPv4-mapped/embedded IPv6 forms, and so are names that only exist on internal networks
/// (<c>localhost</c>, <c>*.local</c>, <c>*.internal</c>, dotless docker service names like
/// <c>postgres</c> or <c>docker-socket-proxy</c>).
/// <para>
/// A check done before the request is not enough on its own (DNS rebinding: the name can resolve to a
/// public address for the check and to 127.0.0.1 for the connect), so callers must also connect only
/// to the addresses returned in <see cref="UrlCheckResult.Addresses"/>.
/// </para>
/// </summary>
public sealed class PublicUrlGuard
{
    // Имена, которые не существуют в публичном DNS и указывают на внутренние сети/сервисы.
    private static readonly string[] BlockedSuffixes =
    {
        "localhost", "local", "internal", "localdomain", "lan", "home", "corp", "intranet", "private",
        "invalid", "test", "example", "home.arpa",
    };

    private readonly IHostAddressResolver _resolver;

    public PublicUrlGuard(IHostAddressResolver resolver)
    {
        _resolver = resolver;
    }

    /// <summary>Validates scheme, credentials, host name and every resolved address of <paramref name="uri"/>.</summary>
    public async Task<UrlCheckResult> CheckAsync(Uri uri, CancellationToken ct = default)
    {
        if (!uri.IsAbsoluteUri)
            return UrlCheckResult.Deny("URL должен быть абсолютным (http:// или https://).");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return UrlCheckResult.Deny($"Схема '{uri.Scheme}' запрещена: разрешены только http и https.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return UrlCheckResult.Deny("URL с логином/паролем (user:pass@host) запрещён.");

        // IdnHost: punycode for international names, bare address (no brackets) for IPv6 literals.
        return await CheckHostAsync(uri.IdnHost, ct);
    }

    /// <summary>Validates a host name or IP literal and resolves it.</summary>
    public async Task<UrlCheckResult> CheckHostAsync(string host, CancellationToken ct = default)
    {
        var normalized = NormalizeHost(host);
        if (normalized.Length == 0)
            return UrlCheckResult.Deny("В URL нет имени хоста.");

        if (IPAddress.TryParse(normalized, out var literal))
        {
            return IsPublicAddress(literal)
                ? UrlCheckResult.Allow(new[] { literal })
                : UrlCheckResult.Deny($"Адрес {literal} не публичный (внутренняя сеть, localhost или служебный диапазон).");
        }

        var nameError = CheckHostName(normalized);
        if (nameError is not null)
            return UrlCheckResult.Deny(nameError);

        IPAddress[] addresses;
        try
        {
            addresses = await _resolver.ResolveAsync(normalized, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return UrlCheckResult.Deny($"Не удалось найти адрес хоста '{normalized}' (DNS).");
        }

        if (addresses.Length == 0)
            return UrlCheckResult.Deny($"Хост '{normalized}' не резолвится ни в один адрес.");

        // ВСЕ адреса должны быть публичными: иначе злоумышленник кладёт в DNS пару
        // «публичный + 127.0.0.1» и выигрывает на выборе адреса при подключении.
        foreach (var address in addresses)
        {
            if (!IsPublicAddress(address))
                return UrlCheckResult.Deny($"Хост '{normalized}' указывает на непубличный адрес {address}.");
        }

        return UrlCheckResult.Allow(addresses);
    }

    /// <summary>Name-level rules that apply before DNS: internal-only names are refused outright.</summary>
    public static string? CheckHostName(string host)
    {
        var normalized = NormalizeHost(host);
        if (normalized.Length == 0)
            return "В URL нет имени хоста.";

        foreach (var suffix in BlockedSuffixes)
        {
            if (normalized == suffix || normalized.EndsWith("." + suffix, StringComparison.Ordinal))
                return $"Хост '{normalized}' — внутреннее имя, оно не доступно из интернета.";
        }

        // Имя без точки — это имя из локальной сети/docker (backend, postgres, docker-socket-proxy),
        // а не публичный сайт.
        if (!normalized.Contains('.'))
            return $"Хост '{normalized}' — имя без домена (внутренний сервис), запрос запрещён.";

        return null;
    }

    /// <summary>
    /// True only for globally routable unicast addresses. IPv4-mapped (<c>::ffff:a.b.c.d</c>), NAT64
    /// (<c>64:ff9b::/96</c>) and 6to4 (<c>2002::/16</c>) forms are judged by the IPv4 address inside.
    /// </summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(address),
            _ => false,
        };
    }

    private static bool IsPublicIPv4(byte[] b)
    {
        if (b.Length != 4) return false;

        return !(
            b[0] == 0 ||                                        // 0.0.0.0/8 "this network"
            b[0] == 10 ||                                       // 10.0.0.0/8
            (b[0] == 100 && (b[1] & 0xC0) == 64) ||             // 100.64.0.0/10 CGNAT
            b[0] == 127 ||                                      // 127.0.0.0/8 loopback
            (b[0] == 169 && b[1] == 254) ||                     // 169.254.0.0/16 link-local, cloud metadata
            (b[0] == 172 && (b[1] & 0xF0) == 16) ||             // 172.16.0.0/12
            (b[0] == 192 && b[1] == 0 && b[2] == 0) ||          // 192.0.0.0/24 IETF protocol assignments
            (b[0] == 192 && b[1] == 0 && b[2] == 2) ||          // 192.0.2.0/24 TEST-NET-1
            (b[0] == 192 && b[1] == 88 && b[2] == 99) ||        // 192.88.99.0/24 6to4 relay
            (b[0] == 192 && b[1] == 168) ||                     // 192.168.0.0/16
            (b[0] == 198 && (b[1] & 0xFE) == 18) ||             // 198.18.0.0/15 benchmarking
            (b[0] == 198 && b[1] == 51 && b[2] == 100) ||       // 198.51.100.0/24 TEST-NET-2
            (b[0] == 203 && b[1] == 0 && b[2] == 113) ||        // 203.0.113.0/24 TEST-NET-3
            b[0] >= 224);                                       // 224/4 multicast, 240/4 reserved, broadcast
    }

    private static bool IsPublicIPv6(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 16) return false;

        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback))
            return false;

        // ::/96 (IPv4-compatible, deprecated) and anything else with the first 96 bits zero.
        if (b.Take(12).All(x => x == 0))
            return false;

        // 64:ff9b::/96 NAT64 — the gateway translates to the embedded IPv4 address.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0))
            return IsPublicIPv4(b[12..16]);

        // Only 2000::/3 is global unicast; this also rejects fc00::/7 (ULA), fe80::/10 (link-local),
        // fec0::/10 (site-local), ff00::/8 (multicast), 100::/64 (discard) and 64:ff9b:1::/48.
        if ((b[0] & 0xE0) != 0x20)
            return false;

        // 2001:db8::/32 documentation.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)
            return false;

        // 2001::/32 Teredo — hides an IPv4 address, refuse outright.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
            return false;

        // 2002::/16 6to4 — the IPv4 address follows the prefix.
        if (b[0] == 0x20 && b[1] == 0x02)
            return IsPublicIPv4(b[2..6]);

        return true;
    }

    private static string NormalizeHost(string host) =>
        (host ?? string.Empty).Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
}
