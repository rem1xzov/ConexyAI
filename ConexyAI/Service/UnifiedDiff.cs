using System.Text;
using System.Text.RegularExpressions;

namespace ConexyAI.Service;

// UNIFIED_DIFF: добавлено 2026-10-04 — агент умеет применять патчи в стандартном формате (как git diff):
// строки с '-' удаляются, с '+' добавляются, '@@' открывает хунк. Точнее и дешевле по токенам, чем
// str_replace на несколько мест, а ошибка (хунк не совпал) сообщается явно, без «найди точную строку».
public static class UnifiedDiff
{
    public sealed record Hunk(
        int OldStart, int OldCount, int NewStart, int NewCount,
        IReadOnlyList<string> OldLines, IReadOnlyList<string> NewLines, bool NewNoNewline);

    public sealed record FilePatch(
        string Path, string? OldPath, bool IsNew, bool IsDelete, List<Hunk> Hunks);

    public sealed record ApplyResult(bool Success, string? Text, int Applied, string? Error);

    private static readonly Regex HunkHeader = new(
        @"^@@\s+-(?<os>\d+)(?:,(?<oc>\d+))?\s+\+(?<ns>\d+)(?:,(?<nc>\d+))?\s+@@",
        RegexOptions.Compiled);

    /// <summary>Parses a unified diff (optionally git-prefixed). Bare hunks use <paramref name="defaultPath"/>.</summary>
    public static List<FilePatch> Parse(string diff, string? defaultPath, out string? error)
    {
        error = null;
        // No file headers at all: attribute every hunk to the caller-provided path.
        if (!diff.Contains("--- ", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(defaultPath))
            diff = $"--- a/{defaultPath}\n+++ b/{defaultPath}\n{diff}";

        var result = new List<FilePatch>();
        FilePatch? current = null;
        HunkBuilder? hunk = null;
        string? oldHeader = null;

        void FinishHunk()
        {
            if (hunk is null) return;
            current ??= new FilePatch(defaultPath ?? string.Empty, null, false, false, new List<Hunk>());
            current.Hunks.Add(hunk.Build());
            hunk = null;
        }

        void FinishFile()
        {
            FinishHunk();
            if (current is not null)
            {
                result.Add(current);
                current = null;
            }
        }

        // A trailing newline produces a final empty element that is not a real (context) line.
        var rawLines = diff.Replace("\r\n", "\n").Split('\n').ToList();
        if (rawLines.Count > 0 && rawLines[^1].Length == 0 && diff.EndsWith("\n", StringComparison.Ordinal))
            rawLines.RemoveAt(rawLines.Count - 1);

        foreach (var raw in rawLines)
        {
            var line = raw;

            if (line.StartsWith("diff --git", StringComparison.Ordinal))
            {
                FinishFile();
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (current is { Hunks.Count: > 0 }) FinishFile();
                else FinishHunk();
                oldHeader = NormalizeHeaderPath(line[4..]);
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var newHeader = NormalizeHeaderPath(line[4..]);
                var isNew = oldHeader == "/dev/null";
                var isDelete = newHeader == "/dev/null";
                var path = isDelete ? oldHeader ?? defaultPath ?? string.Empty : newHeader;
                current = new FilePatch(path, oldHeader, isNew, isDelete, new List<Hunk>());
                oldHeader = null;
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                FinishHunk();
                var m = HunkHeader.Match(line);
                if (!m.Success)
                {
                    error = $"Invalid hunk header: '{line}'.";
                    return result;
                }
                hunk = new HunkBuilder(m);
                continue;
            }

            if (hunk is not null)
            {
                if (line.StartsWith("+", StringComparison.Ordinal)) { hunk.Add(line[1..]); continue; }
                if (line.StartsWith("-", StringComparison.Ordinal)) { hunk.Remove(line[1..]); continue; }
                if (line.StartsWith(" ", StringComparison.Ordinal)) { hunk.Context(line[1..]); continue; }
                if (line.StartsWith("\\", StringComparison.Ordinal)) { hunk.NoNewline(); continue; }
                if (line.Length == 0) { hunk.Context(string.Empty); continue; }
                // Something like "index abc..def" ended the hunk without a new header — close it.
                FinishHunk();
                continue;
            }

            // Metadata lines (index, mode, "Binary files …"): ignored.
        }

        FinishFile();

        if (result.Count == 0)
            error = "No file headers or hunks found in the patch.";
        if (result.Count > 0 && result.All(f => f.Hunks.Count == 0))
            error = "The patch contains no hunks.";

        return result;
    }

    /// <summary>Applies one file's hunks to <paramref name="original"/> (line offsets are tolerated).</summary>
    public static ApplyResult Apply(string original, FilePatch file)
    {
        var crlf = original.Contains("\r\n", StringComparison.Ordinal);
        var trailingNewline = original.EndsWith("\n", StringComparison.Ordinal)
            || original.Length == 0 && file.IsNew;
        var noNewlineAtEnd = file.Hunks.Count > 0 && file.Hunks[^1].NewNoNewline;

        var lines = SplitLines(original);
        var delta = 0;
        var applied = 0;

        foreach (var h in file.Hunks)
        {
            // A zero-length old side is an insertion AFTER OldStart (git convention), not before it.
            var expected = (h.OldCount == 0 ? h.OldStart : h.OldStart - 1) + delta;
            var at = FindBlock(lines, h.OldLines, expected);
            if (at < 0)
            {
                return new ApplyResult(false, null, applied,
                    $"hunk @@ -{h.OldStart},{h.OldCount} +{h.NewStart},{h.NewCount} @@ does not match '{file.Path}'.");
            }

            lines.RemoveRange(at, h.OldLines.Count);
            lines.InsertRange(at, h.NewLines);
            delta += h.NewLines.Count - h.OldLines.Count;
            applied++;
        }

        if (noNewlineAtEnd) trailingNewline = false;
        return new ApplyResult(true, JoinLines(lines, crlf, trailingNewline), applied, null);
    }

    /// <summary>Distinct file paths referenced by a patch (used for the changed-files bookkeeping).</summary>
    public static IReadOnlyList<string> ExtractPaths(string patch, string? defaultPath)
    {
        var files = Parse(patch, defaultPath, out _);
        return files.Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList();
    }

    // ---- helpers ----

    private static int FindBlock(List<string> lines, IReadOnlyList<string> block, int expected)
    {
        if (block.Count == 0) return Math.Clamp(expected, 0, lines.Count);
        if (Matches(lines, block, expected)) return expected;

        for (var distance = 1; distance < lines.Count; distance++)
        {
            if (expected - distance >= 0 && Matches(lines, block, expected - distance)) return expected - distance;
            if (Matches(lines, block, expected + distance)) return expected + distance;
        }
        return -1;
    }

    private static bool Matches(List<string> lines, IReadOnlyList<string> block, int at)
    {
        if (at < 0 || at + block.Count > lines.Count) return false;
        for (var i = 0; i < block.Count; i++)
        {
            if (!string.Equals(lines[at + i], block[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string JoinLines(List<string> lines, bool crlf, bool trailingNewline)
    {
        var newline = crlf ? "\r\n" : "\n";
        var joined = string.Join(newline, lines);
        return trailingNewline ? joined + newline : joined;
    }

    private static string NormalizeHeaderPath(string header)
    {
        var path = header;
        var tab = path.IndexOf('\t');
        if (tab >= 0) path = path[..tab];
        path = path.Trim();
        if (path is "/dev/null" or "a/dev/null" or "b/dev/null") return "/dev/null";
        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
            path = path[2..];
        return path;
    }

    private sealed class HunkBuilder
    {
        private readonly int _oldStart, _oldCount, _newStart, _newCount;
        private readonly List<string> _old = new();
        private readonly List<string> _new = new();
        private bool _newNoNewline;

        public HunkBuilder(Match m)
        {
            _oldStart = Int(m, "os");
            _oldCount = Count(m, "oc");
            _newStart = Int(m, "ns");
            _newCount = Count(m, "nc");
        }

        public void Context(string s) { _old.Add(s); _new.Add(s); }
        public void Add(string s) => _new.Add(s);
        public void Remove(string s) => _old.Add(s);
        public void NoNewline() => _newNoNewline = true;

        public Hunk Build() => new(_oldStart, _oldCount, _newStart, _newCount, _old, _new, _newNoNewline);

        private static int Int(Match m, string group) =>
            m.Groups[group].Success ? int.Parse(m.Groups[group].Value) : 0;

        private static int Count(Match m, string group) =>
            m.Groups[group].Success ? int.Parse(m.Groups[group].Value) : 1;
    }
}
