namespace ConexyAI.Configuration;

/// <summary>Binds the <c>Jwt</c> section of appsettings.json.</summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "ConexyAI";
    public string Audience { get; set; } = "ConexyAI-Clients";
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenLifetimeMinutes { get; set; } = 60;
}
