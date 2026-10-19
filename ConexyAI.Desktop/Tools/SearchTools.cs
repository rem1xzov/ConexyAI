using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace ConexyAI.Desktop.Tools;

/// <summary>grep / glob по рабочей папке.</summary>
public sealed class SearchTools
{
    // TOKEN_ECONOMY: каждая строка — токены в контексте. 80 совпадений достаточно, чтобы найти место.
    private const int DefaultMaxResults = 80;
    private const int HardMaxResults = 400;
    private const long MaxFileBytes = 2_000_000;

    private readonly PathGuard _guard;

    public SearchTools(PathGuard guard) => _guard = guard;

    public ToolResult Grep(ToolArgs args)
    {
        var pattern = args.Required("pattern", "grep");
        var baseDir = string.IsNullOrWhiteSpace(args.Str("path")) ? _guard.Root : _guard.Resolve(args.Str("path"));
        var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        if (args.Bool("ignore_case") == true)
            options |= RegexOptions.IgnoreCase;

        Regex regex;
        try
        {
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            throw new ToolException("Невалидное регулярное выражение: " + ex.Message);
        }

        var max = Math.Clamp(args.Int("max_results") is > 0 and var m ? m : DefaultMaxResults, 1, HardMaxResults);
        var matcher = BuildMatcher(args.Str("glob"));
        var context = Math.Clamp(args.Int("context_lines") ?? 0, 0, 10);
        var mode = args.Str("output_mode") ?? "content";

        var sb = new StringBuilder();
        int found = 0, searched = 0, files = 0;
        var truncated = false;

        // `path` может быть файлом: раньше EnumerateFiles получал файл и возвращал 0 результатов —
        // агент считал, что совпадений нет, и переходил на десятки PowerShell-чтений.
        var candidates = File.Exists(baseDir)
            ? new[] { baseDir }
            : EnumerateFiles(baseDir, matcher, args.Bool("include_ignored") == true);

        foreach (var file in candidates)
        {
            if (new FileInfo(file).Length > MaxFileBytes)
                continue;

            string[] lines;
            try
            {
                var text = File.ReadAllText(file);
                if (text.Contains('\0'))
                    continue;
                lines = text.Split('\n');
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            searched++;
            var relative = _guard.Relative(file);
            var fileHits = 0;
            var lastPrinted = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                bool hit;
                try { hit = regex.IsMatch(line); }
                catch (RegexMatchTimeoutException) { throw new ToolException("Регулярное выражение слишком медленное — упрости его."); }

                if (!hit)
                    continue;

                fileHits++;
                if (mode == "files")
                    break; // достаточно знать, что файл подходит
                if (mode == "count")
                    continue;

                // Как grep -C: строки вокруг совпадения, без повторов; контекст помечен «-», совпадение «:».
                var from = Math.Max(lastPrinted + 1, i - context);
                if (context > 0 && lastPrinted >= 0 && from > lastPrinted + 1)
                    sb.AppendLine("--");
                for (var j = from; j <= Math.Min(lines.Length - 1, i + context); j++)
                {
                    var text = lines[j].TrimEnd('\r');
                    sb.Append(relative).Append(j == i ? ':' : '-').Append(j + 1).Append(j == i ? ": " : "- ")
                      .AppendLine(text.Length > 300 ? text[..300] + "…" : text);
                    lastPrinted = j;
                }
                if (++found >= max)
                {
                    truncated = true;
                    break;
                }
            }

            if (fileHits > 0 && mode is "files" or "count")
            {
                sb.AppendLine(mode == "files" ? relative : $"{relative}: {fileHits}");
                found += fileHits;
                if (++files >= max)
                    truncated = true;
            }

            if (truncated)
                break;
        }

        if (found == 0)
            return ToolResult.Ok($"No matches found (searched {searched} files).");
        if (truncated)
            sb.Append("… (truncated; narrow the search with 'glob' or 'path')");
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    public ToolResult Glob(ToolArgs args)
    {
        var pattern = args.Required("pattern", "glob");
        var baseDir = string.IsNullOrWhiteSpace(args.Str("path")) ? _guard.Root : _guard.Resolve(args.Str("path"));
        var max = Math.Clamp(args.Int("max_results") is > 0 and var m ? m : DefaultMaxResults, 1, HardMaxResults);
        var matcher = BuildMatcher(pattern)!;

        // Сначала сортируем, потом режем: иначе при обрезке получался случайный набор файлов.
        var all = EnumerateFiles(baseDir, matcher, args.Bool("include_ignored") == true).Select(_guard.Relative).Order(StringComparer.Ordinal).ToList();
        if (all.Count == 0)
            return ToolResult.Ok("No files matched.");
        var text = string.Join("\n", all.Take(max));
        return ToolResult.Ok(all.Count > max ? text + $"\n… ({all.Count - max} more; narrow the pattern or 'path')" : text);
    }

    private static Matcher? BuildMatcher(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob))
            return null;
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(glob.Contains('/') || glob.Contains("**") ? glob : "**/" + glob);
        return matcher;
    }

    /// <summary>
    /// Правила корневого .gitignore как glob-исключения (упрощённо: без «!» и вложенных .gitignore).
    /// Чтобы поиск не тонул в сборочных артефактах, кэшах и сгенерированном коде — как ripgrep.
    /// </summary>
    private Matcher? GitIgnore()
    {
        var path = Path.Combine(_guard.Root, ".gitignore");
        if (!File.Exists(path))
            return null;
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        var any = false;
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (IOException) { return null; }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or '!')
                continue;
            var anchored = line.StartsWith('/') || line.TrimEnd('/').Contains('/');
            var pattern = line.Trim('/');
            if (pattern.Length == 0)
                continue;
            var prefix = anchored ? string.Empty : "**/";
            matcher.AddInclude(prefix + pattern + "/**");
            if (!line.EndsWith('/'))
                matcher.AddInclude(prefix + pattern);
            any = true;
        }
        return any ? matcher : null;
    }

    private IEnumerable<string> EnumerateFiles(string dir, Matcher? matcher, bool includeIgnored = false)
    {
        var ignore = includeIgnored ? null : GitIgnore();
        bool Ignored(string full, bool isDir) =>
            ignore is not null && ignore.Match(Path.GetRelativePath(_guard.Root, full).Replace('\\', '/') + (isDir ? "/.x" : string.Empty)).HasMatches;

        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(current).ToList(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (!FileTools.IgnoredDirs.Contains(name) && new DirectoryInfo(entry).LinkTarget is null && !Ignored(entry, isDir: true))
                        stack.Push(entry);
                }
                else if ((matcher is null || matcher.Match(Path.GetRelativePath(dir, entry).Replace('\\', '/')).HasMatches) && !Ignored(entry, isDir: false))
                {
                    yield return entry;
                }
            }
        }
    }
}
