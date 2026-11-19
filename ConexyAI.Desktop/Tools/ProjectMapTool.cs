using System.Text;
using System.Text.RegularExpressions;

namespace ConexyAI.Desktop.Tools;

// REPO_MAP: добавлено 2026-11-19 — в десктоп-режиме проект лежит на ПК, и сервер его не видит.
// Поэтому карту строит клиент: один вызов `repo_map` отдаёт дерево, языки, файлы сборки, точки входа,
// крупнейшие исходники и объявления — вместо десятков `workspace_list_files` / `file_read`.
public sealed partial class ProjectMapTool
{
    private const int MaxFiles = 5_000;
    private const int MaxSymbolFiles = 150;
    private const int SymbolReadChars = 6_000;
    private const int MaxSymbolsPerFile = 12;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "dist", "build", "out", "target", ".next", ".nuxt",
        ".venv", "venv", "__pycache__", ".idea", ".vs", ".gradle", "vendor", "coverage", ".pytest_cache",
        "packages", "pkg", "release", "Debug", "Release", ".cache", "tmp", "temp",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".svg", ".pdf", ".zip", ".gz", ".tar",
        ".7z", ".rar", ".exe", ".dll", ".so", ".dylib", ".woff", ".woff2", ".ttf", ".otf", ".mp3", ".mp4",
        ".mov", ".avi", ".pyc", ".class", ".jar", ".nupkg", ".lockb", ".db", ".sqlite", ".bin",
    };

    private static readonly (string File, string Hint)[] BuildFiles =
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

    private static readonly HashSet<string> EntryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program.cs", "Startup.cs", "main.py", "app.py", "manage.py", "main.go", "main.rs",
        "index.ts", "main.ts", "index.js", "main.js", "App.tsx", "index.tsx", "server.ts",
        "server.js", "main.dart",
    };

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp", [".ts"] = "typescript", [".tsx"] = "typescript", [".js"] = "javascript",
        [".jsx"] = "javascript", [".py"] = "python", [".go"] = "go", [".rs"] = "rust", [".java"] = "java",
        [".kt"] = "kotlin", [".swift"] = "swift", [".php"] = "php", [".rb"] = "ruby", [".cpp"] = "c++",
        [".cc"] = "c++", [".c"] = "c", [".h"] = "c", [".hpp"] = "c++",
    };

    [GeneratedRegex(@"\b(?:class|interface|record|struct|enum|function|def|fn)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled)]
    private static partial Regex DeclarationPattern();

    private readonly string _root;
    private readonly object _sync = new();
    private string? _cachedKey;
    private string? _cachedMap;
    private DateTime _cachedAt;

    public ProjectMapTool(string root)
    {
        _root = root;
    }

    public ToolResult Run(ToolArgs args)
    {
        try
        {
            var path = Normalize(args.Str("path"));
            var depth = Math.Clamp(args.Int("depth") ?? 2, 1, 6);
            var symbols = args.Bool("symbols") ?? true;
            var maxChars = Math.Clamp(args.Int("max_chars") ?? 6_000, 800, 12_000);
            return ToolResult.Ok(Map(path, depth, symbols, maxChars));
        }
        catch (ToolException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ToolResult.Fail($"repo_map: не удалось построить карту проекта: {ex.Message}");
        }
    }

    private string Map(string requestedPath, int depth, bool withSymbols, int maxChars)
    {
        var key = $"{_root}|{requestedPath}|{depth}|{withSymbols}|{maxChars}";
        lock (_sync)
        {
            if (_cachedMap is not null && _cachedKey == key && (DateTime.UtcNow - _cachedAt).TotalSeconds < 20)
                return _cachedMap;
        }

        var files = Walk().OrderBy(file => file, StringComparer.Ordinal).ToList();
        if (files.Count == 0)
            return "[repo_map] проект пуст: файлов нет. Используй workspace_list_files/file_read.";

        var basePath = Path.Combine(_root, requestedPath.Replace('/', Path.DirectorySeparatorChar));
        var prefix = basePath.Length > _root.Length
            ? Path.GetRelativePath(_root, basePath).Replace('\\', '/')
            : string.Empty;
        if (prefix.Length > 0)
            files = files.Where(file => file.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                                        || file.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0)
            return $"[repo_map] в проекте нет пути '{requestedPath}'.";

        var builder = new StringBuilder();
        builder.Append("[repo_map] ").Append(files.Count).Append(" файл(ов)");
        if (prefix.Length > 0)
            builder.Append(" в ").Append(prefix);
        var languages = files
            .Select(file => Path.GetExtension(file).ToLowerInvariant())
            .Where(Languages.ContainsKey)
            .GroupBy(extension => Languages[extension])
            .OrderByDescending(group => group.Count())
            .Take(6)
            .Select(group => $"{group.Key} {group.Count()}");
        if (languages.Any())
            builder.Append(" · языки: ").Append(string.Join(", ", languages));

        var builds = BuildFiles
            .Where(hint => files.Any(file => FileNameMatches(file, hint.File)))
            .Select(hint => hint.Hint)
            .Distinct()
            .Take(3)
            .ToList();
        if (builds.Count > 0)
            builder.Append(" · сборка: ").Append(string.Join("; ", builds));
        builder.Append('\n');

        var roots = files.GroupBy(file => file.Contains('/') ? file[..file.IndexOf('/')] : "(root)")
            .OrderByDescending(group => group.Count())
            .Take(12)
            .Select(group => $"{group.Key}/ ({group.Count()})");
        builder.Append("Верхний уровень: ").Append(string.Join(" · ", roots)).Append('\n');

        var entries = files.Where(file => EntryNames.Contains(Path.GetFileName(file))).Take(8).ToList();
        if (entries.Count > 0)
            builder.Append("Точки входа: ").Append(string.Join(", ", entries)).Append('\n');

        var directories = files
            .Select(file => file.Contains('/') ? file[..file.LastIndexOf('/')] : string.Empty)
            .Where(directory => directory.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(directory => directory, StringComparer.Ordinal)
            .Take(60)
            .ToList();
        if (directories.Count > 0)
        {
            builder.Append("Каталоги:\n");
            foreach (var directory in directories)
                builder.Append("  ").Append(directory).Append('\n');
        }

        var sourceFiles = files.Where(file => Languages.ContainsKey(Path.GetExtension(file))).ToList();
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        var declarations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in sourceFiles.Take(withSymbols ? MaxSymbolFiles : 0))
        {
            try
            {
                var full = Path.Combine(_root, file.Replace('/', Path.DirectorySeparatorChar));
                using var reader = new StreamReader(full, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var head = new char[SymbolReadChars];
                var read = reader.ReadBlock(head, 0, head.Length);
                var text = new string(head, 0, read);
                lines[file] = CountLines(text);
                var names = DeclarationPattern().Matches(text)
                    .Select(match => match.Groups[1].Value)
                    .Distinct(StringComparer.Ordinal)
                    .Take(MaxSymbolsPerFile)
                    .ToList();
                if (names.Count > 0)
                    declarations[file] = names;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Один нечитаемый файл не мешает карте.
            }
        }

        var largest = lines.OrderByDescending(pair => pair.Value).Take(12).ToList();
        if (largest.Count > 0)
        {
            builder.Append("Крупнейшие исходники:\n");
            foreach (var (file, count) in largest)
                builder.Append("  ").Append(file).Append(" (").Append(count).Append(" строк)\n");
        }

        if (declarations.Count > 0)
        {
            builder.Append("Объявления:\n");
            foreach (var (file, names) in declarations.Take(40))
                builder.Append("  ").Append(file).Append(": ").Append(string.Join(", ", names)).Append('\n');
        }

        builder.Append("Дальше используй repo_map(path, depth, symbols) для поддерева и file_read/grep только для нужных файлов.");

        var result = builder.ToString();
        if (result.Length > maxChars)
            result = result[..maxChars] + "\n… (карта обрезана — уточни path)";

        lock (_sync)
        {
            _cachedKey = key;
            _cachedMap = result;
            _cachedAt = DateTime.UtcNow;
        }

        return result;
    }

    private bool FileNameMatches(string file, string pattern)
    {
        var name = Path.GetFileName(file);
        return pattern.StartsWith("*.", StringComparison.Ordinal)
            ? name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> Walk()
    {
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(_root);
        while (stack.Count > 0 && files.Count < MaxFiles)
        {
            var directory = stack.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (files.Count >= MaxFiles)
                    break;

                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (IgnoredDirs.Contains(name) || new DirectoryInfo(entry).LinkTarget is not null)
                        continue;
                    stack.Push(entry);
                }
                else
                {
                    if (BinaryExtensions.Contains(Path.GetExtension(entry)))
                        continue;
                    files.Add(Path.GetRelativePath(_root, entry).Replace('\\', '/'));
                }
            }
        }

        return files;
    }

    private static int CountLines(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
                count++;
        }
        return count + 1;
    }

    private static string Normalize(string? path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
}
