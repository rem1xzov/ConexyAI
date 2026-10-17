namespace ConexyAI.Desktop.Tools;

/// <summary>
/// run_tests / get_diagnostics: тип проекта определяется по файлам в корне, команда строится самим
/// клиентом (поэтому без подтверждения). Явная 'command' от модели идёт через обычный гейт bash.
/// </summary>
public static class ProjectCommands
{
    public static async Task<ToolResult> RunAsync(string root, ShellTool shell, ToolArgs args, bool test, CancellationToken ct)
    {
        if (args.Str("command") is { Length: > 0 } explicitCommand)
            return await shell.RunAsync(args, test ? "run_tests" : "get_diagnostics", ct);

        var command = Detect(root, test)
                      ?? throw new ToolException(
                          "Не удалось определить тип проекта (нет .sln/.csproj, package.json, pyproject.toml, Cargo.toml, go.mod). " +
                          "Передай явную 'command'.");

        return await shell.RunTrustedAsync(command, timeoutSeconds: 1800, ct);
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
