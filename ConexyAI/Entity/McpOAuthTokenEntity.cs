namespace ConexyAI.Entity;

// MCP_OAUTH: добавлено 2026-11-03 — выданные OAuth-токены удалённого MCP-сервера и регистрация клиента.
// ServerKey — нормализованный адрес сервера (хост + путь без завершающего «/»), поэтому одна и та же
// запись подходит и для сохранённого в настройках URL, и для адреса из запроса агента.
// Значения (JSON с токенами) шифруются ISecretProtector: в БД и дампах открытых токенов нет.
public class McpOAuthTokenEntity
{
    public Guid UserId { get; set; }

    /// <summary>Нормализованный адрес MCP-сервера: «host[:port]/path».</summary>
    public string ServerKey { get; set; } = string.Empty;

    /// <summary>JSON с access/refresh-токенами, зашифрованный (base64).</summary>
    public string? Tokens { get; set; }

    /// <summary>JSON динамической регистрации клиента (RFC 7591), зашифрованный (base64).</summary>
    public string? ClientRegistration { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
