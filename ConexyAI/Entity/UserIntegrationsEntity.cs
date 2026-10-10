namespace ConexyAI.Entity;

// USER_INTEGRATIONS: добавлено 2026-10-10 — персональные креды агента (GitHub PAT и личные
// MCP-серверы) хранятся на СЕРВЕРЕ, а не только в браузере. Так они переживают смену устройства,
// доступны фоновым задачам агента и не теряются при очистке localStorage.
//
// Значения шифруются (ISecretProtector, AES-GCM), поэтому в БД, дампах и логах их нет в открытом
// виде: ключ живёт в окружении (SECRETS_KEY или Jwt:SigningKey), а не рядом с данными.
public class UserIntegrationsEntity
{
    public Guid UserId { get; set; }

    /// <summary>Личный GitHub Personal Access Token, зашифрованный (base64).</summary>
    public string? GitHubToken { get; set; }

    /// <summary>Личные MCP-серверы (JSON-массив URL/имя/токен), зашифрованные (base64).</summary>
    public string? McpServers { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
