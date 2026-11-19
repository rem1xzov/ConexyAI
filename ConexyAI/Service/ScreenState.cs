using System.Text.Json;

namespace ConexyAI.Service;

// COMPUTER_USE: добавлено 2026-11-19 — структурированное состояние кадра, которое присылает десктоп
// после каждого действия. Раньше защита от циклов хешировала ТЕКСТ заметки, а в нём всегда были время
// и позиция курсора, поэтому «клик в пустоту» выглядел как успех.
/// <summary>One parsed <c>screen_state:</c> line of a computer-use tool result.</summary>
public sealed record ScreenState(
    string? Frame,
    bool? Changed,
    string? Foreground,
    int? CursorX,
    int? CursorY,
    int? Shot,
    string? Size);

public static class ScreenStateParser
{
    /// <summary>Prefix of the structured line the desktop client appends to a computer-use result.</summary>
    public const string Marker = "screen_state:";

    /// <summary>Parses the last structured state line of a tool output; null when it is absent or broken.</summary>
    public static ScreenState? Parse(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return null;

        var index = output.LastIndexOf(Marker, StringComparison.Ordinal);
        if (index < 0)
            return null;

        var json = output[(index + Marker.Length)..].Trim();
        var newline = json.IndexOfAny(['\n', '\r']);
        if (newline >= 0)
            json = json[..newline];

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            int? cursorX = null;
            int? cursorY = null;
            if (root.TryGetProperty("cursor", out var cursor)
                && cursor.ValueKind == JsonValueKind.Array
                && cursor.GetArrayLength() == 2)
            {
                if (cursor[0].TryGetInt32(out var x)) cursorX = x;
                if (cursor[1].TryGetInt32(out var y)) cursorY = y;
            }

            return new ScreenState(
                Text(root, "frame"),
                Boolean(root, "changed"),
                Text(root, "foreground"),
                cursorX,
                cursorY,
                Number(root, "shot"),
                Text(root, "size"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static bool? Boolean(JsonElement root, string name) => root.TryGetProperty(name, out var element)
        ? element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        }
        : null;

    private static int? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;
}
