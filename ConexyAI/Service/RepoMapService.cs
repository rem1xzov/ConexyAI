using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ConexyAI.Configuration;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// REPO_MAP: добавлено 2026-11-19 — harness строит компактную карту проекта (дерево, языки, файлы
// сборки, точки входа, объявления символов) и отдаёт её агенту в контексте + инструментом `repo_map`.
// Агент перестаёт «сканировать» проект через десятки read/grep, а сразу видит структуру.
public interface IRepoMapService
{
    /// <summary>
    /// The compact map block injected into the agent context, or <c>null</c> when the workspace is empty
    /// or the map is disabled. Rebuilds only when the file set changed or the cache is stale.
    /// </summary>
    Task<string?> GetInjectedBlockAsync(Guid chatId, CancellationToken ct = default);

    /// <summary>
    /// Renders a subtree of the cached map for the agent's <c>repo_map</c> tool.
    /// </summary>
    /// <param name="chatId">Chat workspace id.</param>
    /// <param name="path">Relative directory (or file) to describe; empty for the whole map.</param>
    /// <param name="depth">How many directory levels to expand (1-6).</param>
    /// <param name="withSymbols">Whether declarations of the listed files are included.</param>
    /// <param name="maxChars">Answer cap.</param>
    /// <param name="ct">Cancellation.</param>
    Task<string> QueryAsync(
        Guid chatId,
        string? path,
        int depth,
        bool withSymbols,
        int maxChars,
        CancellationToken ct = default);

    /// <summary>Drops the cached map of one chat after the run changed files.</summary>
    void Invalidate(Guid chatId);
}

public sealed class RepoMapService : IRepoMapService
{
    private static readonly HashSet<string> EntryPointNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program.cs", "Startup.cs", "main.py", "app.py", "manage.py", "main.go", "main.rs",
        "index.ts", "main.ts", "index.js", "main.js", "App.tsx", "index.tsx", "server.ts",
        "server.js", "Program.fs", "main.dart", "cmd/main.go",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".svg", ".pdf",
        ".zip", ".gz", ".tar", ".7z", ".rar", ".exe", ".dll", ".so", ".dylib",
        ".woff", ".woff2", ".ttf", ".otf", ".mp3", ".mp4", ".mov", ".avi",
        ".pyc", ".class", ".jar", ".nupkg", ".pnpm-lock.yaml", ".lockb",
    };

    private static readonly (string File, string Hint)[] BuildFileHints =
    [
        ("*.sln", "dotnet build <sln> / dotnet test <sln>"),
        ("*.csproj", "dotnet build / dotnet test"),
        ("package.json", "npm run build / npm test"),
        ("pyproject.toml", "pytest"),
        ("requirements.txt", "pytest"),
        ("go.mod", "go build ./... / go test ./..."),
        ("Cargo.toml", "cargo build / cargo test"),
        ("Makefile", "make"),
        ("docker-compose.yml", "docker compose up"),
        ("Dockerfile", "docker build"),
        ("tsconfig.json", "tsc --noEmit"),
    ];

    private readonly IConexyWorkspaceService _workspace;
    private readonly RepoMapOptions _options;
    private readonly ILogger<RepoMapService> _logger;
    private readonly ConcurrentDictionary<Guid, MapCache> _cache = new();

    public RepoMapService(
        IConexyWorkspaceService workspace,
        IOptions<RepoMapOptions> options,
        ILogger<RepoMapService> logger)
    {
        _workspace = workspace;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> GetInjectedBlockAsync(Guid chatId, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return null;

        var workspace = _workspace.GetTaskWorkspacePathIfExists(chatId);
        if (workspace is null)
            return null;

        var cache = await EnsureFreshAsync(chatId, ct);
        if (cache is null || cache.Files.Count == 0)
            return null;
        return cache.InjectedBlock;
    }

    public async Task<string> QueryAsync(
        Guid chatId,
        string? path,
        int depth,
        bool withSymbols,
        int maxChars,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return "[repo_map] отключён в настройках сервера.";

        var cache = await EnsureFreshAsync(chatId, ct);
        if (cache is null)
            return "[repo_map] проект ещё пуст: в рабочей области нет файлов. Используйте read/grep/glob.";
        if (cache.Files.Count == 0)
            return "[repo_map] проект ещё пуст: в рабочей области нет файлов. Используйте read/grep/glob.";

        var requested = Normalize(path);
        var budget = Math.Clamp(maxChars <= 0 ? _options.QueryMaxChars : maxChars, 800, Math.Max(800, _options.MaxChars));
        var expanded = Math.Clamp(depth <= 0 ? 2 : depth, 1, 6);

        var matched = cache.Files
            .Where(file => requested.Length == 0
                           || file.Path.Equals(requested, StringComparison.OrdinalIgnoreCase)
                           || file.Path.StartsWith(requested + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matched.Count == 0)
            return $"[repo_map] в проекте нет пути '{requested}'. Корень: {cache.TopLevelSummary()}";

        return RenderSubtree(requested, matched, cache, expanded, withSymbols, budget);
    }

    public void Invalidate(Guid chatId)
    {
        if (_cache.TryGetValue(chatId, out var cache))
            cache.Dirty = true;
    }

    // --- freshness ---------------------------------------------------------------------------

    private async Task<MapCache?> EnsureFreshAsync(Guid chatId, CancellationToken ct)
    {
        var workspace = _workspace.GetTaskWorkspacePathIfExists(chatId);
        if (workspace is null)
        {
            _cache.TryRemove(chatId, out _);
            return null;
        }

        var cache = _cache.GetOrAdd(chatId, _ => new MapCache());
        var listed = await _workspace.ListFilesAsync(chatId, string.Empty, ct);
        if (!listed.Success)
        {
            _logger.LogDebug("repo map: listing failed for chat {ChatId}: {Error}", chatId, listed.Error);
            return cache.BuiltAt == default ? null : cache;
        }

        var files = listed.Files
            .Select(Normalize)
            .Where(file => file.Length > 0 && !ShouldExclude(file))
            .OrderBy(file => file, StringComparer.Ordinal)
            .Take(_options.MaxFiles)
            .ToList();

        var fingerprint = Fingerprint(files);
        var stale = cache.BuiltAt == default
                    || cache.Dirty
                    || cache.Fingerprint != fingerprint
                    || (DateTime.UtcNow - cache.BuiltAt).TotalSeconds > Math.Max(10, _options.RebuildSeconds);
        if (!stale)
            return cache;

        if (cache.BuiltAt != default && cache.Fingerprint == fingerprint && !cache.Dirty)
        {
            // Набор файлов тот же — обновляем только отметку времени, без повторного разбора.
            cache.BuiltAt = DateTime.UtcNow;
            return cache;
        }

        try
        {
            Build(chatId, cache, files, fingerprint, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "repo map: build failed for chat {ChatId}", chatId);
            cache.Dirty = false;
            cache.BuiltAt = DateTime.UtcNow;
        }

        return cache;
    }

    private void Build(Guid chatId, MapCache cache, List<string> files, string fingerprint, CancellationToken ct)
    {
        var parsed = new List<MapFile>(files.Count);
        var symbolBudget = Math.Max(0, _options.MaxSymbolFiles);
        foreach (var path in files)
        {
            if (ct.IsCancellationRequested)
                break;

            var language = SourceSymbolParser.DetectLanguage(path);
            var lines = 0;
            IReadOnlyList<(string Kind, string Name)> symbols = [];

            if (language.Length > 0 && symbolBudget > 0 && !IsLikelyHuge(path))
            {
                symbolBudget--;
                var read = _workspace.ReadFileAsync(chatId, path, ct).GetAwaiter().GetResult();
                if (read.Success && read.Content is { Length: > 0 } content)
                {
                    lines = CountLines(content);
                    var head = content.Length > _options.SymbolReadChars ? content[.._options.SymbolReadChars] : content;
                    try
                    {
                        symbols = SourceSymbolParser.Parse(path, head)
                            .Where(symbol => !string.IsNullOrWhiteSpace(symbol.Name))
                            .Take(Math.Max(1, _options.MaxSymbolsPerFile))
                            .Select(symbol => (symbol.Kind, symbol.Name))
                            .ToList();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "repo map: symbol parse failed for {Path}", path);
                    }
                }
            }

            parsed.Add(new MapFile(path, language, lines, symbols));
        }

        cache.Files = parsed;
        cache.Fingerprint = fingerprint;
        cache.Dirty = false;
        cache.BuiltAt = DateTime.UtcNow;
        cache.InjectedBlock = RenderInjectedBlock(parsed);
        _logger.LogInformation(
            "repo map: built {Files} file(s), {Chars} chars block for chat {ChatId}",
            parsed.Count, cache.InjectedBlock?.Length ?? 0, chatId);
    }

    private static bool IsLikelyHuge(string path) =>
        path.Contains("lock", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".map", StringComparison.OrdinalIgnoreCase);

    private bool ShouldExclude(string relativePath)
    {
        var segments = relativePath.Split('/');
        if (segments.Any(segment => _options.ExcludeDirectories.Any(dir =>
                string.Equals(dir, segment, StringComparison.OrdinalIgnoreCase))))
            return true;
        var extension = Path.GetExtension(relativePath);
        return extension.Length > 0 && BinaryExtensions.Contains(extension);
    }

    // --- rendering ---------------------------------------------------------------------------

    private string? RenderInjectedBlock(IReadOnlyList<MapFile> files)
    {
        if (files.Count == 0)
            return null;

        var builder = new StringBuilder();
        builder.Append("[Project map] ").Append(files.Count).Append(" files");
        var languages = files
            .Where(file => file.Language.Length > 0)
            .GroupBy(file => file.Language, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Take(6)
            .Select(group => $"{group.Key} {group.Count()}");
        if (languages.Any())
            builder.Append(" · languages: ").Append(string.Join(", ", languages));

        var builds = BuildFileHints
            .Where(hint => files.Any(file => Path.GetFileName(file.Path).Equals(
                hint.File.TrimStart('*'), StringComparison.OrdinalIgnoreCase)))
            .Select(hint => hint.Hint)
            .Distinct()
            .Take(3)
            .ToList();
        if (builds.Count > 0)
            builder.Append(" · build: ").Append(string.Join("; ", builds));
        builder.Append('\n');

        var top = TopLevelDirectories(files, out var rootFiles);
        if (top.Count > 0)
        {
            builder.Append("Top-level: ")
                .Append(string.Join(" · ", top.Select(item => $"{item.Directory}/ ({item.Count})")))
                .Append('\n');
        }
        if (rootFiles > 0)
            builder.Append("Root files: ").Append(rootFiles).Append('\n');

        var entries = files
            .Where(file => EntryPointNames.Contains(Path.GetFileName(file.Path)))
            .Select(file => file.Path)
            .Take(6)
            .ToList();
        if (entries.Count > 0)
            builder.Append("Entry points: ").Append(string.Join(", ", entries)).Append('\n');

        // SRC_TREE: дерево второго уровня — агенту видно, где что лежит, без единого read.
        var dirs = files
            .SelectMany(file => DirectoryPaths(file.Path))
            .Where(dir => dir.Length > 0 && dir.Count(ch => ch == '/') >= 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(dir => dir, StringComparer.Ordinal)
            .Take(60)
            .ToList();
        if (dirs.Count > 0)
        {
            builder.Append("Directories:\n");
            foreach (var dir in dirs)
                builder.Append("  ").Append(dir).Append('\n');
        }

        var hotspots = files
            .Where(file => file.Lines > 0)
            .OrderByDescending(file => file.Lines)
            .Take(12)
            .ToList();
        if (hotspots.Count > 0)
        {
            builder.Append("Largest sources:\n");
            foreach (var file in hotspots)
            {
                builder.Append("  ").Append(file.Path).Append(" (").Append(file.Lines).Append(" lines)");
                if (file.Symbols.Count > 0)
                    builder.Append(" — ").Append(string.Join(", ", file.Symbols.Take(5).Select(SymbolLabel)));
                builder.Append('\n');
            }
        }

        var withSymbols = files.Where(file => file.Symbols.Count > 0).Take(40).ToList();
        if (withSymbols.Count > 0)
        {
            builder.Append("Symbols:\n");
            foreach (var file in withSymbols)
            {
                builder.Append("  ").Append(file.Path).Append(": ")
                    .Append(string.Join(", ", file.Symbols.Take(_options.MaxSymbolsPerFile).Select(SymbolLabel)))
                    .Append('\n');
            }
        }

        builder.Append("Use tool `repo_map` (path, depth, symbols) for a subtree, then read/grep only the files you need.");
        return Clamp(builder.ToString(), Math.Max(2000, _options.MaxChars));
    }

    private string RenderSubtree(
        string requested,
        IReadOnlyList<MapFile> files,
        MapCache cache,
        int depth,
        bool withSymbols,
        int maxChars)
    {
        var prefix = requested.Length == 0 ? string.Empty : requested + "/";
        var baseDepth = requested.Length == 0 ? 0 : requested.Count(ch => ch == '/') + 1;
        var builder = new StringBuilder();
        builder.Append("[repo_map] ").Append(files.Count).Append(" file(s)");
        if (requested.Length > 0)
            builder.Append(" under ").Append(requested);
        builder.Append(" · ").Append(cache.TopLevelSummary()).Append('\n');

        var byDir = files
            .GroupBy(file => DirectoryOf(file.Path, baseDepth, depth))
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in byDir)
        {
            var label = group.Key.Length == 0 ? "(root)" : group.Key;
            builder.Append(label).Append('/').Append(" (").Append(group.Count()).Append(")\n");
            foreach (var file in group.OrderBy(file => file.Path, StringComparer.Ordinal).Take(40))
            {
                var name = file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? file.Path[prefix.Length..]
                    : file.Path;
                builder.Append("  ").Append(name);
                if (file.Lines > 0)
                    builder.Append(" (").Append(file.Lines).Append(" lines)");
                if (withSymbols && file.Symbols.Count > 0)
                    builder.Append(" — ").Append(string.Join(", ", file.Symbols.Select(SymbolLabel)));
                builder.Append('\n');
                if (builder.Length > maxChars)
                    break;
            }
            if (builder.Length > maxChars)
                break;
        }

        return Clamp(builder.ToString(), maxChars);
    }

    // --- helpers -----------------------------------------------------------------------------

    private static string DirectoryOf(string path, int baseDepth, int depth)
    {
        var segments = path.Split('/');
        var take = Math.Min(segments.Length - 1, baseDepth + depth);
        return take <= 0 ? string.Empty : string.Join('/', segments.Take(take));
    }

    private static IEnumerable<string> DirectoryPaths(string path)
    {
        var segments = path.Split('/');
        for (var index = 1; index < segments.Length; index++)
            yield return string.Join('/', segments.Take(index));
    }

    private static List<(string Directory, int Count)> TopLevelDirectories(IReadOnlyList<MapFile> files, out int rootFiles)
    {
        rootFiles = files.Count(file => !file.Path.Contains('/'));
        return files
            .Where(file => file.Path.Contains('/'))
            .GroupBy(file => file.Path[..file.Path.IndexOf('/')], StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.Key, group.Count()))
            .OrderByDescending(item => item.Item2)
            .Take(10)
            .ToList();
    }

    private static string SymbolLabel((string Kind, string Name) symbol) =>
        symbol.Kind.Length == 0 ? symbol.Name : $"{symbol.Name} ({symbol.Kind})";

    private static int CountLines(string content)
    {
        var count = 0;
        foreach (var ch in content)
        {
            if (ch == '\n')
                count++;
        }
        return count + 1;
    }

    private static string Normalize(string? path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

    private static string Fingerprint(IReadOnlyList<string> files)
    {
        var text = string.Join('\n', files);
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string Clamp(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "\n… (карта обрезана, уточните путь)";

    private sealed record MapFile(
        string Path,
        string Language,
        int Lines,
        IReadOnlyList<(string Kind, string Name)> Symbols);

    private sealed class MapCache
    {
        public IReadOnlyList<MapFile> Files { get; set; } = [];
        public string? InjectedBlock { get; set; }
        public string Fingerprint { get; set; } = string.Empty;
        public DateTime BuiltAt { get; set; }
        public bool Dirty { get; set; }

        public string TopLevelSummary()
        {
            var top = Files
                .Where(file => file.Path.Contains('/'))
                .GroupBy(file => file.Path[..file.Path.IndexOf('/')], StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Take(6)
                .Select(group => $"{group.Key}/ {group.Count()}");
            return string.Join(" · ", top);
        }
    }
}
