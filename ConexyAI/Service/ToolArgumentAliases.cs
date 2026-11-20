using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConexyAI.Service;

// TOOL_ARG_ALIASES: добавлено 2026-11-21 — модель иногда называет путь не `path`, а `file`,
// `file_path`, `filename` и т. п. Инструмент отвечал «requires 'path'» и вызов тратился впустую
// (в стресс-тесте PWA это случилось 3 раза). Ремонтируем только обязательный `path` и только для
// файловых инструментов: остальные поля не трогаем, неизвестное не выдумываем.
public static class ToolArgumentAliases
{
    private static readonly string[] PathAliases = ["file_path", "filepath", "file", "filename", "file_name"];

    private static readonly HashSet<string> PathTools = new(StringComparer.Ordinal)
    {
        "file_read", "file_write", "file_write_bytes", "file_patch", "apply_patch", "str_replace_editor",
        "create_document", "read_document_file", "view_image", "pc_read_file", "pc_list_dir",
        "pc_copy_to_project", "download_file",
    };

    /// <summary>Возвращает исправленный JSON, если обязательный path можно восстановить из алиаса.</summary>
    public static bool TryRepair(string tool, string? arguments, out string repaired)
    {
        repaired = string.Empty;
        if (!PathTools.Contains(tool) || string.IsNullOrWhiteSpace(arguments))
            return false;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(arguments);
        }
        catch (JsonException)
        {
            return false;
        }

        if (node is not JsonObject obj)
            return false;
        if (obj["path"] is JsonValue existing && existing.TryGetValue<string>(out var current) && !string.IsNullOrWhiteSpace(current))
            return false;

        foreach (var alias in PathAliases)
        {
            if (obj[alias] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                obj["path"] = text;
                repaired = obj.ToJsonString();
                return true;
            }
        }

        return false;
    }
}
