using System.Text;
using System.Text.RegularExpressions;
using ConexyAI.Contract;
using Microsoft.Extensions.FileSystemGlobbing;

namespace ConexyAI.Service;

// SEARCH_REPLACE: добавлено 2026-10-05 — глобальный поиск/замена по проекту. Обход файлов повторяет
// правила grep-инструмента агента: та же jail рабочей области, симлинки не разыменовываются, бинарные
// (NUL) и слишком большие файлы пропускаются, суммарно сканируется не больше ~32 МБ за запрос.
public interface IProjectSearchService
{
    Task<ProjectSearchResult> SearchAsync(Guid chatId, ProjectSearchRequest request, CancellationToken ct = default);

    Task<ProjectReplaceResult> ReplaceAsync(Guid chatId, ProjectReplaceRequest request, CancellationToken ct = default);
}

public class ConexyProjectSearchService : IProjectSearchService
{
    private const int MaxFileBytes = 2_000_000;
    private const long MaxScanBytes = 32L * 1024 * 1024;
    private const int DefaultMaxResults = 500;
    private const int HardMaxResults = 2000;
    private const int MaxQueryLength = 1000;
    private const int MaxReplacementLength = 10_000;
    private const int MaxFilesChanged = 500;
    private const int MaxTotalReplacements = 20_000;
    private const int PreviewLength = 400;

    private readonly IConexyWorkspaceService _workspace;
    private readonly ISandboxActivity _activity;
    private readonly ILogger<ConexyProjectSearchService> _logger;

    public ConexyProjectSearchService(
        IConexyWorkspaceService workspace,
        ISandboxActivity activity,
        ILogger<ConexyProjectSearchService> logger)
    {
        _workspace = workspace;
        _activity = activity;
        _logger = logger;
    }

    public async Task<ProjectSearchResult> SearchAsync(Guid chatId, ProjectSearchRequest request, CancellationToken ct = default)
    {
        var query = request.Query ?? string.Empty;
        if (query.Length == 0)
            return SearchError("EMPTY_QUERY");
        if (query.Length > MaxQueryLength)
            return SearchError("QUERY_TOO_LONG");

        // A brand-new chat (no workspace yet) simply has nothing to search.
        var root = _workspace.GetTaskWorkspacePathIfExists(chatId);
        if (root is null)
            return new ProjectSearchResult(true, Array.Empty<SearchMatch>(), 0, 0, false, null);

        if (!TryBuildRegex(query, request.Regex, request.CaseSensitive, out var regex))
            return SearchError("INVALID_PATTERN");

        var matcher = BuildMatcher(request.IncludeGlob, out var basenameOnly);
        var limit = Math.Clamp(request.MaxResults <= 0 ? DefaultMaxResults : request.MaxResults, 1, HardMaxResults);
        var realRoot = WorkspaceJail.GetRealPath(root);

        var matches = new List<SearchMatch>();
        var filesSearched = 0;
        var scannedChars = 0L;
        var truncated = false;

        foreach (var file in EnumerateWorkspaceFiles(root))
        {
            ct.ThrowIfCancellationRequested();

            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes) continue;
            if (scannedChars + info.Length > MaxScanBytes)
            {
                truncated = true;
                break;
            }

            var relative = ToSlash(Path.GetRelativePath(realRoot, file));
            if (matcher is not null && !GlobMatches(matcher, basenameOnly, relative)) continue;

            var content = await ReadTextAsync(root, relative, ct);
            if (content is null) continue; // binary or unreadable

            scannedChars += content.Length;
            filesSearched++;

            var lineNumber = 0;
            foreach (var rawLine in content.Split('\n'))
            {
                lineNumber++;
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0) continue;

                foreach (Match match in regex.Matches(line))
                {
                    matches.Add(new SearchMatch(
                        relative,
                        lineNumber,
                        match.Index + 1,
                        match.Length,
                        line.Length > PreviewLength ? line[..PreviewLength] : line));

                    if (matches.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }
                }

                if (truncated) break;
            }

            if (truncated) break;
        }

        return new ProjectSearchResult(true, matches, matches.Count, filesSearched, truncated, null);
    }

    public async Task<ProjectReplaceResult> ReplaceAsync(Guid chatId, ProjectReplaceRequest request, CancellationToken ct = default)
    {
        var query = request.Query ?? string.Empty;
        if (query.Length == 0)
            return ReplaceError("EMPTY_QUERY");
        if (query.Length > MaxQueryLength)
            return ReplaceError("QUERY_TOO_LONG");

        var replacement = request.Replacement ?? string.Empty;
        if (replacement.Length > MaxReplacementLength)
            return ReplaceError("REPLACEMENT_TOO_LONG");

        var root = _workspace.GetTaskWorkspacePathIfExists(chatId);
        if (root is null)
            return ReplaceError("NO_WORKSPACE");

        if (!TryBuildRegex(query, request.Regex, request.CaseSensitive, out var regex))
            return ReplaceError("INVALID_PATTERN");

        // Replacing writes files, so it must not race a command inside the sandbox.
        using var slot = await _activity.AcquireAsync(chatId, ct);

        var realRoot = WorkspaceJail.GetRealPath(root);
        var matcher = BuildMatcher(request.IncludeGlob, out var basenameOnly);

        // A single-file replace (from a result group) or every matching file.
        List<string> targets;
        var singleFile = !string.IsNullOrWhiteSpace(request.Path);
        if (singleFile)
        {
            var normalized = NormalizeRelative(request.Path);
            if (normalized is null)
                return ReplaceError("INVALID_PATH");
            targets = new List<string> { normalized };
        }
        else
        {
            targets = EnumerateWorkspaceFiles(root)
                .Select(file => new { file, info = new FileInfo(file) })
                .Where(x => x.info.Length <= MaxFileBytes)
                .Select(x => ToSlash(Path.GetRelativePath(realRoot, x.file)))
                .Where(relative => matcher is null || GlobMatches(matcher, basenameOnly, relative))
                .ToList();
        }

        var results = new List<ReplaceFileResult>();
        var total = 0;

        foreach (var relative in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (singleFile && !File.Exists(Path.Combine(realRoot, relative.Replace('/', Path.DirectorySeparatorChar))))
                return ReplaceError("FILE_NOT_FOUND");

            var content = await ReadTextAsync(root, relative, ct);
            if (content is null) continue; // binary or unreadable

            var count = 0;
            var updated = regex.Replace(content, match =>
            {
                count++;
                // Regex mode supports $1/$& group references; plain mode replaces literally.
                return request.Regex ? match.Result(replacement) : replacement;
            });

            if (count == 0) continue;

            try
            {
                await WorkspaceJail.WriteAllTextAsync(root, relative, updated, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Project replace could not write {Path}", relative);
                continue;
            }

            results.Add(new ReplaceFileResult(relative, count));
            total += count;

            if (results.Count >= MaxFilesChanged || total >= MaxTotalReplacements)
                break;
        }

        return new ProjectReplaceResult(true, results.Count, total, results, null);
    }

    private static bool TryBuildRegex(string query, bool isRegex, bool caseSensitive, out Regex regex)
    {
        var options = RegexOptions.Compiled | RegexOptions.CultureInvariant |
                      (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            regex = new Regex(isRegex ? query : Regex.Escape(query), options, TimeSpan.FromSeconds(2));
            return true;
        }
        catch (ArgumentException)
        {
            regex = null!;
            return false;
        }
    }

    private static async Task<string?> ReadTextAsync(string root, string relative, CancellationToken ct)
    {
        try
        {
            var bytes = await WorkspaceJail.ReadAllBytesAsync(root, relative, ct);
            if (bytes.Length > MaxFileBytes) return null;
            if (Array.IndexOf(bytes, (byte)0) >= 0) return null; // binary
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateWorkspaceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", WorkspaceJail.NoLinks(recursive: true));

    private static Matcher? BuildMatcher(string? pattern, out bool basenameOnly)
    {
        basenameOnly = pattern is not null && !pattern.Contains('/');
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(ToSlash(pattern.Trim()));
        return matcher;
    }

    private static bool GlobMatches(Matcher matcher, bool basenameOnly, string relativePath) =>
        matcher.Match(basenameOnly ? Path.GetFileName(relativePath) : relativePath).HasMatches;

    private static string? NormalizeRelative(string? path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.Contains('\0')) return null;
        foreach (var segment in normalized.Split('/'))
        {
            if (segment == "..") return null;
        }
        return normalized;
    }

    private static string ToSlash(string path) => path.Replace('\\', '/');

    private static ProjectSearchResult SearchError(string error) =>
        new(false, Array.Empty<SearchMatch>(), 0, 0, false, error);

    private static ProjectReplaceResult ReplaceError(string error) =>
        new(false, 0, 0, Array.Empty<ReplaceFileResult>(), error);
}
