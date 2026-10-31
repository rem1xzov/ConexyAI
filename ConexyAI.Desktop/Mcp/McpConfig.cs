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

    /// <summary>
    /// Секреты в зашифрованном виде (DPAPI). В args и env на них ссылаются как <c>${secret:ИМЯ}</c> — так токен
    /// не лежит в mcp.json открытым текстом. Другие клиенты (Claude Desktop) это поле просто пропустят.
    /// </summary>
    [JsonPropertyName("conexySecrets")]
    public Dictionary<string, string>? ProtectedSecrets { get; set; }

    /// <summary>
    /// Сколько секунд ждать запуска (0 — 60). У удалённых серверов через mcp-remote первый запуск ждёт входа в браузере —
    /// человеку нужно больше минуты.
    /// </summary>
    [JsonPropertyName("conexyStartupSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int StartupSeconds { get; set; }

    /// <summary>Из каталога (/mcp catalog) — для статуса в списке.</summary>
    [JsonPropertyName("conexyCatalog")]
    public string? CatalogId { get; set; }

    public void SetSecret(string name, string value) =>
        (ProtectedSecrets ??= new())[name] = SecretBox.Protect(value);

    /// <summary>Подставляет расшифрованные секреты вместо ${secret:ИМЯ}.</summary>
    public string Expand(string value)
    {
        if (!value.Contains("${secret:", StringComparison.Ordinal))
            return value;
        foreach (var (name, protectedValue) in ProtectedSecrets ?? [])
        {
            var token = "${secret:" + name + "}";
            if (value.Contains(token, StringComparison.Ordinal))
                value = value.Replace(token, SecretBox.Unprotect(protectedValue)
                    ?? throw new InvalidOperationException($"секрет {name} не расшифрован — добавьте сервер заново (/mcp add)"));
        }
        return value;
    }
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
        // UNIX_0600: в mcp.json токены серверов (на Linux/macOS — лишь base64), читать их может только владелец.
        ClientConfig.WriteOwnerOnly(FilePath, JsonSerializer.Serialize(this, Options));
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
