using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace ConexyAI.Desktop.Tools;

/// <summary>grep / glob по рабочей папке.</summary>
public sealed class SearchTools
{
    private const int DefaultMaxResults = 200;
    private const int HardMaxResults = 1_000;
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

        var sb = new StringBuilder();
        int found = 0, searched = 0;
        var truncated = false;

        foreach (var file in EnumerateFiles(baseDir, matcher))
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
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                bool hit;
                try { hit = regex.IsMatch(line); }
                catch (RegexMatchTimeoutException) { throw new ToolException("Регулярное выражение слишком медленное — упрости его."); }

                if (!hit)
                    continue;

                sb.Append(_guard.Relative(file)).Append(':').Append(i + 1).Append(": ")
                  .AppendLine(line.Length > 300 ? line[..300] + "…" : line);
                if (++found >= max)
                {
                    truncated = true;
                    break;
                }
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

        var result = EnumerateFiles(baseDir, matcher).Take(max).Select(_guard.Relative).Order(StringComparer.Ordinal).ToList();
        return ToolResult.Ok(result.Count == 0 ? "No files matched." : string.Join("\n", result));
    }

    private static Matcher? BuildMatcher(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob))
            return null;
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(glob.Contains('/') || glob.Contains("**") ? glob : "**/" + glob);
        return matcher;
    }

    private IEnumerable<string> EnumerateFiles(string dir, Matcher? matcher)
    {
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
                    if (!FileTools.IgnoredDirs.Contains(name) && new DirectoryInfo(entry).LinkTarget is null)
                        stack.Push(entry);
                }
                else if (matcher is null || matcher.Match(Path.GetRelativePath(dir, entry).Replace('\\', '/')).HasMatches)
                {
                    yield return entry;
                }
            }
        }
    }
}
