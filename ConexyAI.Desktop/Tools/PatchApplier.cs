using System.Text;
using System.Text.RegularExpressions;

namespace ConexyAI.Desktop.Tools;

/// <summary>
/// apply_patch: unified diff в формате git. Модели часто путают номера строк и счётчики в заголовках
/// hunk'ов, поэтому позиция ищется по содержимому (контекст + удаляемые строки) рядом с ожидаемой,
/// а счётчики из заголовка не используются. Все файлы вычисляются в памяти и записываются только
/// если применились ВСЕ hunk'и — патч либо целиком, либо никак.
/// </summary>
public sealed partial class PatchApplier
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private sealed class Hunk
    {
        public int OldStart;
        public List<string> Old = new();
        public List<string> New = new();
    }

    private sealed class FilePatch
    {
        public string? OldPath;
        public string? NewPath;
        public List<Hunk> Hunks = new();
    }

    private readonly PathGuard _guard;

    public PatchApplier(PathGuard guard) => _guard = guard;

    public ToolResult Apply(string patch, string? fallbackPath)
    {
        var files = Parse(patch, fallbackPath);
        if (files.Count == 0)
            throw new ToolException("В патче нет ни одного hunk'а (@@ ... @@). Нужен unified diff.");

        var writes = new List<(string Path, string? Content)>(); // Content == null → удалить
        var summary = new StringBuilder();

        foreach (var file in files)
        {
            var isDelete = file.NewPath is null;
            var targetRel = file.NewPath ?? file.OldPath ?? throw new ToolException("В патче не указан файл.");
            var target = _guard.ResolveForWrite(targetRel);

            if (isDelete)
            {
                if (!File.Exists(target))
                    throw new ToolException($"Удаляемый файл '{targetRel}' не найден.");
                writes.Add((target, null));
                summary.AppendLine($"deleted {targetRel}");
                continue;
            }

            var isCreate = file.OldPath is null;
            string original;
            if (isCreate)
            {
                if (File.Exists(target))
                    throw new ToolException($"Патч создаёт '{targetRel}', но файл уже существует.");
                original = string.Empty;
            }
            else
            {
                if (!File.Exists(target))
                    throw new ToolException($"Файл '{targetRel}' не найден. Для нового файла используй '--- /dev/null'.");
                original = File.ReadAllText(target);
            }

            writes.Add((target, ApplyHunks(original, file.Hunks, targetRel, isCreate)));
            summary.AppendLine($"{(isCreate ? "created" : "patched")} {targetRel} ({file.Hunks.Count} hunk(s))");
        }

        foreach (var (path, content) in writes)
        {
            if (content is null)
            {
                File.Delete(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Utf8NoBom);
        }

        return ToolResult.Ok("Patch applied:\n" + summary.ToString().TrimEnd());
    }

    private static string ApplyHunks(string original, List<Hunk> hunks, string label, bool isCreate)
    {
        var eol = original.Contains("\r\n") ? "\r\n" : "\n";
        var normalized = original.Replace("\r\n", "\n");
        var endsWithNewline = isCreate || normalized.EndsWith('\n');
        var body = normalized.EndsWith('\n') ? normalized[..^1] : normalized;
        var lines = original.Length == 0 ? new List<string>() : body.Split('\n').ToList();

        var delta = 0;
        var number = 0;
        foreach (var hunk in hunks)
        {
            number++;
            var expected = Math.Max(0, hunk.OldStart - 1 + delta);

            int position;
            if (hunk.Old.Count == 0)
            {
                // Чистая вставка: после строки OldStart (0 — в начало).
                position = Math.Clamp(hunk.OldStart + delta, 0, lines.Count);
            }
            else
            {
                position = Find(lines, hunk.Old, expected, exact: true);
                if (position < 0)
                    position = Find(lines, hunk.Old, expected, exact: false);
                if (position < 0)
                    throw new ToolException(
                        $"Hunk #{number} не применился к '{label}': контекст не найден. " +
                        "Перечитай файл (file_read) и составь патч по актуальному содержимому.");
            }

            lines.RemoveRange(position, hunk.Old.Count);
            lines.InsertRange(position, hunk.New);
            delta += hunk.New.Count - hunk.Old.Count;
        }

        var text = string.Join(eol, lines);
        return endsWithNewline && lines.Count > 0 ? text + eol : text;
    }

    private static int Find(List<string> lines, List<string> block, int expected, bool exact)
    {
        var max = lines.Count - block.Count;
        if (max < 0)
            return -1;

        // Ближайшее к ожидаемой позиции совпадение: 0, +1, -1, +2, -2 …
        for (var distance = 0; distance <= Math.Max(expected, max - expected) + 1; distance++)
        {
            foreach (var candidate in distance == 0 ? new[] { expected } : new[] { expected + distance, expected - distance })
            {
                if (candidate < 0 || candidate > max)
                    continue;
                if (Matches(lines, block, candidate, exact))
                    return candidate;
            }
        }

        return -1;
    }

    private static bool Matches(List<string> lines, List<string> block, int start, bool exact)
    {
        for (var i = 0; i < block.Count; i++)
        {
            var a = lines[start + i];
            var b = block[i];
            if (exact ? a != b : a.TrimEnd() != b.TrimEnd())
                return false;
        }
        return true;
    }

    private static List<FilePatch> Parse(string patch, string? fallbackPath)
    {
        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var files = new List<FilePatch>();
        FilePatch? current = null;
        Hunk? hunk = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.StartsWith("--- ", StringComparison.Ordinal) && i + 1 < lines.Length && lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                current = new FilePatch
                {
                    OldPath = CleanPath(line[4..]),
                    NewPath = CleanPath(lines[i + 1][4..]),
                };
                files.Add(current);
                hunk = null;
                i++;
                continue;
            }

            // Служебные строки git (в теле hunk'а такие строки всегда начинаются с ' ', '+' или '-').
            if (line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("new file mode", StringComparison.Ordinal) || line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                hunk = null;
                continue;
            }

            var header = HunkHeader().Match(line);
            if (header.Success)
            {
                if (current is null)
                {
                    if (string.IsNullOrWhiteSpace(fallbackPath))
                        throw new ToolException("Патч без заголовков файла: передай параметр 'path'.");
                    current = new FilePatch { OldPath = fallbackPath, NewPath = fallbackPath };
                    files.Add(current);
                }

                hunk = new Hunk { OldStart = int.Parse(header.Groups[1].Value) };
                current.Hunks.Add(hunk);
                continue;
            }

            if (hunk is null)
                continue;

            if (line.StartsWith('\\'))
                continue; // "\ No newline at end of file"

            if (line.StartsWith('+'))
                hunk.New.Add(line[1..]);
            else if (line.StartsWith('-'))
                hunk.Old.Add(line[1..]);
            else if (line.StartsWith(' '))
            {
                hunk.Old.Add(line[1..]);
                hunk.New.Add(line[1..]);
            }
            else if (line.Length == 0)
            {
                // Пустая строка: либо пустой контекст (редактор срезал пробел), либо хвост патча.
                if (i < lines.Length - 1 && !IsBoundary(lines, i + 1))
                {
                    hunk.Old.Add(string.Empty);
                    hunk.New.Add(string.Empty);
                }
            }
        }

        return files.Where(f => f.Hunks.Count > 0 || f.NewPath is null).ToList();
    }

    private static bool IsBoundary(string[] lines, int index)
    {
        for (var i = index; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
                continue;
            return lines[i].StartsWith("@@", StringComparison.Ordinal)
                   || lines[i].StartsWith("diff ", StringComparison.Ordinal)
                   || lines[i].StartsWith("--- ", StringComparison.Ordinal);
        }
        return true; // дальше только пустые строки
    }

    private static string? CleanPath(string raw)
    {
        var path = raw.Split('\t')[0].Trim().Trim('"');
        if (path == "/dev/null")
            return null;
        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
            path = path[2..];
        return path;
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+\d+(?:,\d+)? @@")]
    private static partial Regex HunkHeader();
}
