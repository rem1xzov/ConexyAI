using System.Text;

namespace ConexyAI.Service.Auth;

// JWT_KEY_GUARD: добавлено 2026-09-24 (ревью L12)
/// <summary>
/// Refuses to start the app outside Development with a missing, placeholder or short
/// <c>Jwt:SigningKey</c>: anyone who knows the committed placeholder could otherwise mint admin
/// tokens for the whole platform.
/// </summary>
public static class JwtSigningKeyGuard
{
    /// <summary>HMAC-SHA256 needs a key of at least 256 bits.</summary>
    public const int MinKeyBytes = 32;

    /// <summary>Placeholder values committed in appsettings.json / .env.example (current and past).</summary>
    private static readonly string[] KnownPlaceholders =
    {
        "change-me-to-a-long-random-secret-of-at-least-32-bytes",
    };

    /// <summary>
    /// Returns <paramref name="signingKey"/> when it is acceptable for <paramref name="environment"/>;
    /// otherwise throws <see cref="InvalidOperationException"/> with an actionable message.
    /// </summary>
    public static string Validate(string? signingKey, IHostEnvironment environment)
    {
        const string howTo = "Set the JWT_SIGNING_KEY environment variable (Jwt__SigningKey) to a random secret, " +
                             "e.g. the output of `openssl rand -base64 48`.";

        if (string.IsNullOrWhiteSpace(signingKey))
            throw new InvalidOperationException($"Jwt:SigningKey is not configured. {howTo}");

        // В Development допускаем плейсхолдер из appsettings.json, чтобы локальный запуск работал из коробки.
        if (environment.IsDevelopment())
            return signingKey;

        var trimmed = signingKey.Trim();
        if (KnownPlaceholders.Any(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase))
            || trimmed.Contains("change-me", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Jwt:SigningKey is still the committed placeholder; refusing to start in '{environment.EnvironmentName}'. {howTo}");
        }

        if (Encoding.UTF8.GetByteCount(signingKey) < MinKeyBytes)
        {
            throw new InvalidOperationException(
                $"Jwt:SigningKey is shorter than {MinKeyBytes} bytes; refusing to start in '{environment.EnvironmentName}'. {howTo}");
        }

        return signingKey;
    }
}
