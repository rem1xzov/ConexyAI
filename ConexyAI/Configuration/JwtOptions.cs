namespace ConexyAI.Configuration;

/// <summary>Binds the <c>Jwt</c> section of appsettings.json.</summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "ConexyAI";
    public string Audience { get; set; } = "ConexyAI-Clients";
    public string SigningKey { get; set; } = string.Empty;
    // EMAIL_AUTH: добавлено 2026-09-19; SESSION_LIFETIME: изменено 2026-09-26 — 129600 minutes =
    // 90 days. The session cookie (conexy_auth) lives exactly as long as the JWT, so the user stays
    // logged in across browser restarts without re-authenticating. Значение переопределяется
    // переменной окружения `Jwt__AccessTokenLifetimeMinutes` — менять его на сервере можно без
    // пересборки фронта.
    public int AccessTokenLifetimeMinutes { get; set; } = 129600;
}
