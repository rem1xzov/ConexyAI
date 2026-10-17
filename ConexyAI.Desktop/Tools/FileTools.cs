using System.Text;

namespace ConexyAI.Desktop.Tools;

/// <summary>file_read / file_write / file_patch / workspace_list_files.</summary>
public sealed class FileTools
{
    internal const int MaxReadChars = 200_000;
    internal const long MaxReadBytes = 5_000_000;
    internal const int MaxListEntries = 2_000;

    internal static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "__pycache__", ".venv", "target",
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly PathGuard _guard;

    public FileTools(PathGuard guard) => _guard = guard;

    public ToolResult Read(ToolArgs args)
    {
        var path = _guard.Resolve(args.Required("path", "file_read"));
        if (Directory.Exists(path))
            throw new ToolException($"'{args.Str("path")}' — папка, а не файл. Используй workspace_list_files.");
        if (!File.Exists(path))
            throw new ToolException($"Файл '{args.Str("path")}' не найден.");
        if (new FileInfo(path).Length > MaxReadBytes)
            throw new ToolException("Файл слишком большой для чтения целиком. Используй grep или str_replace_editor view с view_range.");

        var text = File.ReadAllText(path);
        if (text.Contains('\0'))
            throw new ToolException("Похоже на двоичный файл — читать как текст нельзя.");

        return ToolResult.Ok(text.Length > MaxReadChars
            ? text[..MaxReadChars] + "\n[... файл обрезан ...]"
            : text);
    }

    public ToolResult Write(ToolArgs args)
    {
        var path = _guard.ResolveForWrite(args.Required("path", "file_write"));
        var content = args.Str("content") ?? string.Empty;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteText(path, content);
        return ToolResult.Ok($"File '{args.Str("path")}' written.");
    }

    public ToolResult Patch(ToolArgs args)
    {
        var path = _guard.ResolveForWrite(args.Required("path", "file_patch"));
        var search = args.Required("search_block", "file_patch");
        var replace = args.Str("replace_block") ?? string.Empty;
        if (!File.Exists(path))
            throw new ToolException($"Файл '{args.Str("path")}' не найден.");

        var original = File.ReadAllText(path);
        var updated = ReplaceUnique(original, search, replace, args.Str("path")!);
        WriteText(path, updated);
        return ToolResult.Ok($"Patched '{args.Str("path")}'.");
    }

    /// <summary>Пишет UTF-8, сохраняя BOM, если он был у файла (иначе правка даёт лишний diff в первой строке).</summary>
    internal static void WriteText(string path, string content) =>
        File.WriteAllText(path, content, HasUtf8Bom(path) ? new UTF8Encoding(true) : Utf8NoBom);

    private static bool HasUtf8Bom(string path)
    {
        if (!File.Exists(path))
            return false;
        Span<byte> head = stackalloc byte[3];
        using var stream = File.OpenRead(path);
        return stream.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }

    /// <summary>Приводит переводы строк вставки к стилю файла, чтобы не получить смесь CRLF и LF.</summary>
    private static string MatchLineEndings(string text, string replace)
    {
        var lf = replace.Replace("\r\n", "\n");
        return text.Contains("\r\n") ? lf.Replace("\n", "\r\n") : lf;
    }

    /// <summary>Заменяет ровно одно вхождение; допускает расхождение в окончаниях строк (CRLF/LF).</summary>
    internal static string ReplaceUnique(string text, string search, string replace, string label)
    {
        var count = CountOccurrences(text, search);
        if (count == 1)
            return ReplaceFirst(text, search, MatchLineEndings(text, replace));
        if (count > 1)
            throw new ToolException($"Блок найден {count} раз в '{label}' — добавь контекста, чтобы он был уникальным.");

        // Файл с CRLF, а модель прислала LF (или наоборот).
        if (text.Contains("\r\n") && !search.Contains("\r\n"))
        {
            var crlfSearch = search.Replace("\n", "\r\n");
            var crlfCount = CountOccurrences(text, crlfSearch);
            if (crlfCount == 1)
                return ReplaceFirst(text, crlfSearch, MatchLineEndings(text, replace));
            if (crlfCount > 1)
                throw new ToolException($"Блок найден {crlfCount} раз в '{label}' — добавь контекста, чтобы он был уникальным.");
        }

        throw new ToolException($"Блок не найден в '{label}'. Перечитай файл (file_read) и повтори с точным текстом.");
    }

    public ToolResult List(ToolArgs args)
    {
        var dir = string.IsNullOrWhiteSpace(args.Str("path")) ? _guard.Root : _guard.Resolve(args.Str("path"));
        if (!Directory.Exists(dir))
            throw new ToolException($"Папка '{args.Str("path")}' не найдена.");

        var files = new List<string>();
        Walk(dir, files);
        if (files.Count == 0)
            return ToolResult.Ok("Workspace is empty.");

        files.Sort(StringComparer.Ordinal);
        var text = string.Join("\n", files);
        return ToolResult.Ok(files.Count >= MaxListEntries ? text + "\n… (список обрезан)" : text);
    }

    private void Walk(string dir, List<string> files)
    {
        if (files.Count >= MaxListEntries)
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (files.Count >= MaxListEntries)
                return;

            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (IgnoredDirs.Contains(name) || new DirectoryInfo(entry).LinkTarget is not null)
                    continue;
                Walk(entry, files);
            }
            else
            {
                files.Add(_guard.Relative(entry));
            }
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        if (value.Length == 0)
            return 0;
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string search, string replace)
    {
        var index = text.IndexOf(search, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, index), replace, text.AsSpan(index + search.Length));
    }
}
