namespace ConexyAI.Desktop.Tools;

/// <summary>
/// run_tests / get_diagnostics: тип проекта определяется по файлам в корне, команда строится самим
/// клиентом (поэтому без подтверждения). Явная 'command' от модели идёт через обычный гейт bash.
/// BUILD_FILES_GUARD: но сами команды запускают скрипты проекта (scripts в package.json, цели MSBuild,
/// Makefile, conftest.py…). Если агент в этом сеансе менял такие файлы, запуск — только с подтверждения.
/// </summary>
public static class ProjectCommands
{
    public static async Task<ToolResult> RunAsync(string root, ShellTool shell, ToolArgs args, bool test, CancellationToken ct,
        bool requireApproval = false, Action? onApproved = null)
    {
        if (args.Str("command") is { Length: > 0 } explicitCommand)
            return await shell.RunAsync(args, test ? "run_tests" : "get_diagnostics", ct);

        var command = Detect(root, test)
                      ?? throw new ToolException(
                          "Не удалось определить тип проекта (нет .sln/.csproj, package.json, pyproject.toml, Cargo.toml, go.mod). " +
                          "Передай явную 'command'.");

        if (requireApproval)
        {
            if (!await shell.ApproveAsync(test ? "Запуск тестов" : "Сборка/диагностика",
                    $"{command}\n(в этом сеансе агент менял файлы сборки/тестов — скрипты проекта выполнят его код)", ct))
                return ToolResult.Fail("Пользователь отклонил запуск: агент менял файлы сборки/тестов. Не повторяй; спроси, что делать.");
            onApproved?.Invoke();
        }

        return await shell.RunTrustedAsync(command, timeoutSeconds: 1800, ct);
    }

    private static readonly HashSet<string> BuildFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "makefile", "gnumakefile", "pyproject.toml", "setup.py", "setup.cfg", "conftest.py", "pytest.ini",
        "tox.ini", "noxfile.py", "global.json", "nuget.config", ".npmrc", ".yarnrc", ".yarnrc.yml", ".pnpmfile.cjs", "cargo.toml",
        "build.rs", "go.mod", "go.work", "pom.xml", "gradle.properties", "gradle-wrapper.properties", "gradlew", "gradlew.bat",
        "tsconfig.json", ".babelrc", "sitecustomize.py", "usercustomize.py",
    };

    private static readonly string[] BuildExtensions =
    [
        ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".sln", ".slnx", ".mk", ".gradle", ".gradle.kts",
    ];

    private static readonly string[] BuildConfigPrefixes =
    [
        "directory.build.", "directory.packages.", "jest.config.", "vitest.config.", "vitest.workspace.", "vite.config.",
        "babel.config.", "jest.setup.", "vitest.setup.",
    ];

    /// <summary>Файл, который задаёт, ЧТО выполнят run_tests/get_diagnostics (а не просто исходник проекта).</summary>
    public static bool IsBuildDefinition(string fullPath, string root)
    {
        var name = Path.GetFileName(fullPath);
        if (BuildFileNames.Contains(name)
            || BuildExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            || BuildConfigPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return true;
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        // node_modules/.bin (npx --no-install tsc), .cargo/config.toml, а также .py в корне: `python -m pytest`
        // ставит корень первым в sys.path, и pytest.py/compileall.py агента подменили бы модуль.
        return relative.Split('/').Any(p => p.Equals("node_modules", StringComparison.OrdinalIgnoreCase) || p.Equals(".cargo", StringComparison.OrdinalIgnoreCase))
               || (!relative.Contains('/') && name.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Отпечаток файлов сборки в корне (имя, размер, время) — ловит правки через терминал.</summary>
    public static string Fingerprint(string root)
    {
        try
        {
            return string.Join('|', Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                .Where(f => IsBuildDefinition(f, root))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FileInfo(f))
                .Select(i => $"{i.Name}:{i.Length}:{i.LastWriteTimeUtc.Ticks}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    internal static string? Detect(string root, bool test)
    {
        bool Has(string pattern) => Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any();

        if (Has("*.sln") || Has("*.slnx") || Has("*.csproj"))
            return test ? "dotnet test --nologo" : "dotnet build --nologo";

        if (File.Exists(Path.Combine(root, "package.json")))
        {
            if (test)
                return "npm test --silent";
            return File.Exists(Path.Combine(root, "tsconfig.json")) ? "npx --no-install tsc --noEmit" : "npm run build --if-present";
        }

        if (File.Exists(Path.Combine(root, "Cargo.toml")))
            return test ? "cargo test" : "cargo check";

        if (File.Exists(Path.Combine(root, "go.mod")))
            return test ? "go test ./..." : "go vet ./...";

        if (File.Exists(Path.Combine(root, "pyproject.toml")) || File.Exists(Path.Combine(root, "requirements.txt")))
            return test ? "python -m pytest -q" : "python -m compileall -q .";

        return null;
    }
}
