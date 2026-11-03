namespace ConexyAI.Configuration;

// MCP_OAUTH: добавлено 2026-11-03 — авторизация удалённого MCP-сервера по OAuth 2.1 (spec 2025-06-18).
// Раньше пользователь мог только вставить готовый Bearer-токен; теперь ConexyAI проходит
// authorization code + PKCE в браузере и хранит выданные токены (с refresh) на сервере.
/// <summary>Binds the <c>McpOAuth</c> section of appsettings.json.</summary>
public class McpOAuthOptions
{
    public const string SectionName = "McpOAuth";

    /// <summary>OWNER_ACTION: публичный адрес сайта для redirect_uri (можно задать переменной окружения).</summary>
    public const string PublicBaseUrlEnvVar = "MCP_OAUTH_PUBLIC_BASE_URL";

    /// <summary>Внешний адрес ConexyAI: из него собирается redirect_uri регистрации клиента.</summary>
    public string PublicBaseUrl { get; set; } = "https://conexyai.ru";

    /// <summary>Путь callback-эндпоинта; регистрируется у сервера авторизации как redirect_uri.</summary>
    public string CallbackPath { get; set; } = "/api/mcp/oauth/callback";

    /// <summary>Имя клиента в динамической регистрации (RFC 7591).</summary>
    public string ClientName { get; set; } = "ConexyAI";

    /// <summary>Итоговый redirect_uri, который уходит и в регистрацию, и в authorize/token-запросы.</summary>
    public string RedirectUri => PublicBaseUrl.TrimEnd('/') + CallbackPath;
}
