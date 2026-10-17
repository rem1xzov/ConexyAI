using System.Text.Json;
using System.Text.RegularExpressions;
using ConexyAI.Contract;

namespace ConexyAI.Service.Device;

// LOCAL_MCP: добавлено 2026-10-17 — инструменты, которые предлагает сам десктоп (локальные MCP-серверы и
// встроенный коннектор Google). Схемы приходят от клиента, поэтому чистим: только своё пространство имён
// local__*, ограниченный размер и число — чтобы клиент не мог подменить серверный инструмент или раздуть промпт.
public static partial class DeviceToolSanitizer
{
    public const string Prefix = "local__";
    private const int MaxTools = 48;
    private const int MaxDescription = 600;
    private const int MaxSchemaChars = 8_000;

    [GeneratedRegex("^local__[a-z0-9_]{1,24}__[A-Za-z0-9_\\-]{1,40}$")]
    private static partial Regex NamePattern();

    public static bool IsDeviceTool(string name) => name.StartsWith(Prefix, StringComparison.Ordinal);

    public static List<DeviceToolSpec>? Clean(List<DeviceToolSpec>? tools)
    {
        if (tools is null || tools.Count == 0)
            return null;

        var result = new List<DeviceToolSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (result.Count >= MaxTools)
                break;
            var name = tool.Name?.Trim();
            if (name is null || name.Length > 64 || !NamePattern().IsMatch(name) || !seen.Add(name))
                continue;

            JsonElement schema;
            if (tool.InputSchema is { ValueKind: JsonValueKind.Object } s && s.GetRawText().Length <= MaxSchemaChars)
                schema = s.Clone();
            else
                schema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

            var description = new string((tool.Description ?? string.Empty).Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
            if (description.Length > MaxDescription)
                description = description[..MaxDescription] + "…";

            result.Add(new DeviceToolSpec { Name = name, Description = description, InputSchema = schema });
        }
        return result.Count == 0 ? null : result;
    }
}
