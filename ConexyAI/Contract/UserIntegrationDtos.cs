namespace ConexyAI.Contract;

// USER_INTEGRATIONS: добавлено 2026-10-10 — контракты для серверного хранения персональных кредов.

/// <summary>Status of the user's stored GitHub token (never the token itself).</summary>
public record GitHubTokenStatusDto(bool Configured);

/// <summary>Body of <c>PUT /api/user/github</c>.</summary>
public record SaveGitHubTokenRequest(string? Token);

/// <summary>Body/response of <c>GET|PUT /api/user/mcp</c>: the user's personal MCP servers.</summary>
public record McpServersDto(List<McpServerInput> Servers);
