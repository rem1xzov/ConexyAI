namespace ConexyAI.Contract;

// MCP_OAUTH: добавлено 2026-11-03 — контракты авторизации личного MCP-сервера в браузере.

/// <summary>Body of <c>POST /api/mcp/oauth/start</c>.</summary>
/// <param name="ServerId">Id сохранённого MCP-сервера (для названия в callback-странице).</param>
/// <param name="Url">Адрес MCP-сервера; проверяется тем же SSRF-гардом, что и сохранение.</param>
/// <param name="ResourceMetadataUrl">Необязательный resource_metadata из 401/403 агента.</param>
public record McpOAuthStartRequest(string? ServerId, string? Url, string? ResourceMetadataUrl);

/// <summary>Response of <c>POST /api/mcp/oauth/start</c>.</summary>
public record McpOAuthStartResponse(string AuthorizeUrl);

/// <summary>Response of <c>GET /api/mcp/oauth/status</c>.</summary>
public record McpOAuthStatusDto(bool Authorized, DateTimeOffset? ExpiresAt);
