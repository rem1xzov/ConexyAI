namespace ConexyAI.Configuration;

// MCP: добавлено 2026-10-04 — удалённые MCP-серверы (Streamable HTTP). Уровень A: только HTTP, без
// запуска локальных процессов (stdio потребовал бы постоянного процесса/песочницы).
/// <summary>Binds the <c>Mcp</c> section of appsettings.json.</summary>
public class McpOptions
{
    public const string SectionName = "Mcp";

    public List<McpServerOptions> Servers { get; set; } = new();
}

public class McpServerOptions
{
    /// <summary>Stable id used in tool names (mcp__&lt;id&gt;__&lt;tool&gt;); derived from Name when empty.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable name shown to the model and in the UI.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Streamable-HTTP endpoint, e.g. https://mcp.notion.com/mcp.</summary>
    public string Url { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Extra headers (usually Authorization). Values support <c>${ENV_VAR}</c> so secrets stay in the
    /// environment and never in appsettings.json, the repository or the image.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int TimeoutSeconds { get; set; } = 30;
}
