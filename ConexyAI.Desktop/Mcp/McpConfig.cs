using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConexyAI.Desktop.Mcp;

public sealed class McpServerEntry
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public List<string>? Args { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }
}

/// <summary>
/// Локальные MCP-серверы: %APPDATA%\ConexyAI\mcp.json в том же формате, что у Claude Desktop/Cursor
/// ({"mcpServers": {"имя": {"command": "...", "args": [...], "env": {...}}}}) — готовые конфиги можно копировать.
/// </summary>
public sealed class McpConfig
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerEntry> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConexyAI", "mcp.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static McpConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var config = JsonSerializer.Deserialize<McpConfig>(File.ReadAllText(FilePath), Options) ?? new McpConfig();
                config.Servers = new Dictionary<string, McpServerEntry>(config.Servers, StringComparer.OrdinalIgnoreCase);
                return config;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Ui.Error($"mcp.json не прочитан: {ex.Message}");
        }
        return new McpConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }

    /// <summary>Имя сервера в имени инструмента: латиница в нижнем регистре, цифры и «_».</summary>
    public static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_').ToArray();
        var slug = new string(chars).Trim('_');
        while (slug.Contains("__"))
            slug = slug.Replace("__", "_");
        return slug.Length == 0 ? "server" : slug.Length > 24 ? slug[..24] : slug;
    }
}
