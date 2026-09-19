namespace ConexyAI.Configuration;

/// <summary>Binds the <c>Jwt</c> section of appsettings.json.</summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "ConexyAI";
    public string Audience { get; set; } = "ConexyAI-Clients";
    public string SigningKey { get; set; } = string.Empty;
    // EMAIL_AUTH: добавлено 2026-09-19 — 43200 minutes = 30 days (remember-me session). The
    // session cookie (conexy_auth) lives exactly as long as the JWT, so the user stays logged
    // in across browser restarts without re-authenticating.
    public int AccessTokenLifetimeMinutes { get; set; } = 43200;
}
