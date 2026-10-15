using System.Text;

namespace ConexyAI.Desktop.Tools;

/// <summary>str_replace_editor: view | create | str_replace | insert | undo.</summary>
public sealed class EditorTool
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly PathGuard _guard;
    private readonly FileTools _files;
    // Последняя версия файла до правки — для undo (в памяти, на время сессии).
    private readonly Dictionary<string, string?> _undo = new();

    public EditorTool(PathGuard guard, FileTools files)
    {
        _guard = guard;
        _files = files;
    }

    public ToolResult Run(ToolArgs args)
    {
        var command = args.Required("command", "str_replace_editor");
        var rawPath = args.Required("path", "str_replace_editor");

        return command switch
        {
            "view" => View(rawPath, args),
            "create" => Create(rawPath, args),
            "str_replace" => StrReplace(rawPath, args),
            "insert" => Insert(rawPath, args),
            "undo" => Undo(rawPath),
            _ => throw new ToolException($"Неизвестная команда '{command}'. Допустимо: view, create, str_replace, insert, undo."),
        };
    }

    private ToolResult View(string rawPath, ToolArgs args)
    {
        var path = _guard.Resolve(rawPath);
        if (Directory.Exists(path))
            return _files.List(ToolArgs.Parse($"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(rawPath)}}}"));
        if (!File.Exists(path))
            throw new ToolException($"Файл '{rawPath}' не найден.");

        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
        int from = 1, to = lines.Length;
        if (args.IntArray("view_range") is { Length: 2 } range)
        {
            from = Math.Max(1, range[0]);
            to = range[1] < 0 ? lines.Length : Math.Min(lines.Length, range[1]);
            if (from > to)
                throw new ToolException($"Неверный view_range: файл содержит {lines.Length} строк.");
        }

        var sb = new StringBuilder();
        for (var i = from; i <= to; i++)
            sb.Append(i.ToString().PadLeft(6)).Append('\t').AppendLine(lines[i - 1]);

        var text = sb.ToString();
        return ToolResult.Ok(text.Length > FileTools.MaxReadChars ? text[..FileTools.MaxReadChars] + "\n[... обрезано, используй view_range ...]" : text);
    }

    private ToolResult Create(string rawPath, ToolArgs args)
    {
        var path = _guard.ResolveForWrite(rawPath);
        if (File.Exists(path))
            throw new ToolException($"Файл '{rawPath}' уже существует. Для правки используй str_replace.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, args.Str("file_text") ?? string.Empty, Utf8NoBom);
        _undo[path] = null;
        return ToolResult.Ok($"File '{rawPath}' created.");
    }

    private ToolResult StrReplace(string rawPath, ToolArgs args)
    {
        var path = _guard.ResolveForWrite(rawPath);
        if (!File.Exists(path))
            throw new ToolException($"Файл '{rawPath}' не найден.");

        var oldStr = args.Required("old_str", "str_replace");
        var original = File.ReadAllText(path);
        var updated = FileTools.ReplaceUnique(original, oldStr, args.Str("new_str") ?? string.Empty, rawPath);
        _undo[path] = original;
        File.WriteAllText(path, updated, Utf8NoBom);
        return ToolResult.Ok($"Replaced in '{rawPath}'.");
    }

    private ToolResult Insert(string rawPath, ToolArgs args)
    {
        var path = _guard.ResolveForWrite(rawPath);
        if (!File.Exists(path))
            throw new ToolException($"Файл '{rawPath}' не найден.");

        var original = File.ReadAllText(path);
        var eol = original.Contains("\r\n") ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n").Split('\n').ToList();
        var line = args.Int("insert_line") ?? throw new ToolException("insert requires 'insert_line'.");
        if (line < 0 || line > lines.Count)
            throw new ToolException($"insert_line {line} вне диапазона 0..{lines.Count}.");

        lines.InsertRange(line, (args.Str("new_str") ?? string.Empty).Replace("\r\n", "\n").Split('\n'));
        _undo[path] = original;
        File.WriteAllText(path, string.Join(eol, lines), Utf8NoBom);
        return ToolResult.Ok($"Inserted into '{rawPath}' after line {line}.");
    }

    private ToolResult Undo(string rawPath)
    {
        var path = _guard.ResolveForWrite(rawPath);
        if (!_undo.TryGetValue(path, out var previous))
            throw new ToolException($"Нет сохранённой версии '{rawPath}' для отката.");

        if (previous is null)
            File.Delete(path);
        else
            File.WriteAllText(path, previous, Utf8NoBom);
        _undo.Remove(path);
        return ToolResult.Ok($"Reverted '{rawPath}'.");
    }
}
