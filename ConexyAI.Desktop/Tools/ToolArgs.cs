using System.Text.Json;

namespace ConexyAI.Desktop.Tools;

/// <summary>Результат инструмента, который уходит на сервер и дальше модели.</summary>
public sealed record ToolResult(string Output, bool IsError)
{
    public static ToolResult Ok(string output) => new(output, false);
    public static ToolResult Fail(string output) => new(output, true);
}

/// <summary>Безопасный доступ к JSON-аргументам инструмента.</summary>
public sealed class ToolArgs
{
    private readonly JsonElement _root;

    private ToolArgs(JsonElement root) => _root = root;

    public static ToolArgs Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return new ToolArgs(doc.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            throw new ToolException("Аргументы инструмента — невалидный JSON: " + ex.Message);
        }
    }

    public string? Str(string name) =>
        _root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public string Required(string name, string tool) =>
        Str(name) is { Length: > 0 } s ? s : throw new ToolException($"{tool} requires '{name}'.");

    public int? Int(string name) =>
        _root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    public bool? Bool(string name) =>
        _root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    public Dictionary<string, string> Dict(string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            foreach (var p in v.EnumerateObject())
                result[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.GetRawText();
        return result;
    }

    public string[]? StrArray(string name)
    {
        if (_root.ValueKind != JsonValueKind.Object || !_root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return null;
        return v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray();
    }

    public int[]? IntArray(string name)
    {
        if (_root.ValueKind != JsonValueKind.Object || !_root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return null;
        return v.EnumerateArray().Where(e => e.TryGetInt32(out _)).Select(e => e.GetInt32()).ToArray();
    }
}
