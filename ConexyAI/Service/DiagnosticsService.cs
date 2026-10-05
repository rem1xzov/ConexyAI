using System.Text.Json;
using System.Text.RegularExpressions;
using ConexyAI.Contract;

namespace ConexyAI.Service;

// DIAGNOSTICS: добавлено 2026-10-04 — инструмент get_diagnostics. В песочнице запускается
// проверка только текущего проекта (tsc --noEmit, dotnet build, cargo check, go vet, pyright/mypy/ruff,
// eslint), а вывод парсится в структурированные ошибки с файлом:строкой:колонкой. Приложение и тесты
// не запускаются. Полноценный LSP (hover/типы/автодополнение) потребовал бы постоянного процесса с
// языковым сервером и выполняет код рабочей области на хосте — это противоречит модели безопасности.
public interface IDiagnosticsService
{
    Task<DiagnosticCommandDetection> DetectCommandAsync(Guid chatId, string? path, string? tool, CancellationToken ct = default);

    DiagnosticTool ResolveTool(string? name);

    DiagnosticReport Parse(DiagnosticTool tool, string output, bool truncated);
}

public class ConexyDiagnosticsService : IDiagnosticsService
{
    private readonly IConexyWorkspaceService _workspaceService;

    public ConexyDiagnosticsService(IConexyWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    public DiagnosticTool ResolveTool(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "dotnet" or "build" or "msbuild" or "csharp" or "c#" => DiagnosticTool.DotnetBuild,
        "typescript" or "tsc" or "ts" => DiagnosticTool.TypeScript,
        "eslint" or "js" or "javascript" => DiagnosticTool.Eslint,
        "cargo" or "rust" or "rustc" => DiagnosticTool.Cargo,
        "go" or "govet" or "go-vet" => DiagnosticTool.GoVet,
        "pyright" or "python" or "py" => DiagnosticTool.Pyright,
        "mypy" => DiagnosticTool.Mypy,
        "ruff" => DiagnosticTool.Ruff,
        _ => DiagnosticTool.Unknown,
    };

    public async Task<DiagnosticCommandDetection> DetectCommandAsync(Guid chatId, string? path, string? tool, CancellationToken ct = default)
    {
        var directory = string.IsNullOrWhiteSpace(path) ? string.Empty : path!.Trim();
        var list = await _workspaceService.ListFilesAsync(chatId, directory, ct);
        if (!list.Success)
            return new DiagnosticCommandDetection(null, DiagnosticTool.Unknown, list.Error);

        var files = list.Files.Select(f => f.Replace('\\', '/')).ToList();
        bool Has(string name) => files.Any(f =>
            f.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));

        var forced = ResolveTool(tool);
        if (!string.IsNullOrWhiteSpace(tool))
        {
            var command = CommandFor(forced, Has);
            return command is null
                ? new DiagnosticCommandDetection(null, forced, $"No check command is known for tool '{tool}'.")
                : new DiagnosticCommandDetection(command, forced, null);
        }

        if (Has("tsconfig.json"))
            return new DiagnosticCommandDetection(CommandFor(DiagnosticTool.TypeScript, Has), DiagnosticTool.TypeScript, null);

        if (Has(".eslintrc") || Has(".eslintrc.json") || Has(".eslintrc.js") || Has(".eslintrc.cjs") || Has("eslint.config.js") || Has("eslint.config.mjs"))
            return new DiagnosticCommandDetection(CommandFor(DiagnosticTool.Eslint, Has), DiagnosticTool.Eslint, null);

        if (files.Any(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            return new DiagnosticCommandDetection(CommandFor(DiagnosticTool.DotnetBuild, Has), DiagnosticTool.DotnetBuild, null);

        if (Has("Cargo.toml"))
            return new DiagnosticCommandDetection(CommandFor(DiagnosticTool.Cargo, Has), DiagnosticTool.Cargo, null);

        if (Has("go.mod"))
            return new DiagnosticCommandDetection(CommandFor(DiagnosticTool.GoVet, Has), DiagnosticTool.GoVet, null);

        if (Has("pyrightconfig.json"))
            return new DiagnosticCommandDetection(CommandFor(DiagnosticTool.Pyright, Has), DiagnosticTool.Pyright, null);
        if (Has("mypy.ini") || Has(".mypy.ini") || Has("setup.cfg"))
            return new DiagnosticCommandDetection(CommandFor(DiagnosticTool.Mypy, Has), DiagnosticTool.Mypy, null);
        if (Has("ruff.toml") || Has(".ruff.toml"))
            return new DiagnosticCommandDetection(CommandFor(DiagnosticTool.Ruff, Has), DiagnosticTool.Ruff, null);
        if (Has("pyproject.toml") || files.Any(f => f.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
            return new DiagnosticCommandDetection(CommandFor(DiagnosticTool.Pyright, Has), DiagnosticTool.Pyright, null);

        return new DiagnosticCommandDetection(null, DiagnosticTool.Unknown,
            "Could not detect a check command. Pass 'tool' (dotnet, tsc, eslint, cargo, go, pyright, mypy, ruff) or an explicit 'command'.");
    }

    private static string? CommandFor(DiagnosticTool tool, Func<string, bool> has) => tool switch
    {
        DiagnosticTool.DotnetBuild => "dotnet build --nologo -v q -clp:NoSummary",
        DiagnosticTool.TypeScript => has("node_modules/.bin/tsc")
            ? "node_modules/.bin/tsc --noEmit --pretty false"
            : "npx --no-install tsc --noEmit --pretty false",
        DiagnosticTool.Eslint => has("node_modules/.bin/eslint")
            ? "node_modules/.bin/eslint . -f json"
            : "npx --no-install eslint . -f json",
        DiagnosticTool.Cargo => "cargo check --message-format=short",
        DiagnosticTool.GoVet => "go vet ./...",
        DiagnosticTool.Pyright => "python -m pyright",
        DiagnosticTool.Mypy => "python -m mypy .",
        DiagnosticTool.Ruff => "python -m ruff check . --output-format concise",
        _ => null,
    };

    public DiagnosticReport Parse(DiagnosticTool tool, string output, bool truncated) =>
        DiagnosticParser.Parse(tool, output, truncated);
}

/// <summary>Parses compiler/linter output into structured diagnostics across the common toolchains.</summary>
internal static class DiagnosticParser
{
    // MSBuild / tsc: File.cs(12,5): error CS0103: message
    private static readonly Regex ParenStyle = new(
        @"^(?<file>[^(]+)\((?<line>\d+)(?:,(?<col>\d+))?\):\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)?:?\s*(?<msg>.+)$",
        RegexOptions.Compiled);

    // gcc / cargo short: src/main.rs:3:5: error[E0308]: message  (severity optional, e.g. go vet)
    private static readonly Regex ColonStyle = new(
        @"^(?<file>[^:]+):(?<line>\d+)(?::(?<col>\d+))?:\s*(?:(?<sev>error|warning|note)(?:\[(?<code>[^\]]+)\])?:\s*)?(?<msg>.+)$",
        RegexOptions.Compiled);

    // pyright: /path/file.py:12:5 - error: message
    private static readonly Regex DashStyle = new(
        @"^(?<file>[^:]+):(?<line>\d+):(?<col>\d+)\s*-\s*(?<sev>error|warning|information):\s*(?<msg>.+)$",
        RegexOptions.Compiled);

    // mypy: file.py:12: error: message  [code]
    private static readonly Regex MypyStyle = new(
        @"^(?<file>[^:]+):(?<line>\d+):\s*(?<sev>error|warning|note):\s*(?<msg>.+?)(?:\s+\[(?<code>[^\]]+)\])?$",
        RegexOptions.Compiled);

    private static readonly string[] NoisePrefixes =
    {
        "Build succeeded", "Build FAILED", "Determining projects", "Time Elapsed",
        "0 Warning", "0 Error", "Restored ", "warning NU",
    };

    public static DiagnosticReport Parse(DiagnosticTool tool, string output, bool truncated)
    {
        var text = output ?? string.Empty;
        var diagnostics = new List<Diagnostic>();

        if (tool == DiagnosticTool.Eslint)
        {
            if (!TryParseEslintJson(text, diagnostics))
                ParseLines(text, tool, diagnostics);
        }
        else
        {
            ParseLines(text, tool, diagnostics);
        }

        // Deduplicate identical locations/messages (some tools repeat them across projects).
        var unique = diagnostics
            .GroupBy(d => (d.File, d.Line, d.Column, d.Severity, d.Code, d.Message))
            .Select(g => g.First())
            .Take(500)
            .ToList();

        var errors = unique.Count(d => d.Severity == "error");
        var warnings = unique.Count(d => d.Severity == "warning");

        return new DiagnosticReport(tool, true, errors, warnings, unique, Tail(text, 40), truncated);
    }

    private static void ParseLines(string text, DiagnosticTool tool, List<Diagnostic> diagnostics)
    {
        foreach (var raw in text.Split('\n'))
        {
            // Some tools indent diagnostics (pyright); match on the trimmed line.
            var line = raw.TrimEnd('\r').TrimStart();
            if (line.Length == 0) continue;
            if (NoisePrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal))) continue;

            var diagnostic = Match(line, tool);
            if (diagnostic is not null) diagnostics.Add(diagnostic);
        }
    }

    private static Diagnostic? Match(string line, DiagnosticTool tool)
    {
        var m = ParenStyle.Match(line);
        if (m.Success) return Build(m, tool, "error");

        m = DashStyle.Match(line);
        if (m.Success) return Build(m, tool, "error");

        m = MypyStyle.Match(line);
        if (m.Success) return Build(m, tool, "error");

        m = ColonStyle.Match(line);
        if (m.Success) return Build(m, tool, "error");

        return null;
    }

    private static Diagnostic Build(Match m, DiagnosticTool tool, string defaultSeverity)
    {
        var severity = m.Groups["sev"].Success ? NormalizeSeverity(m.Groups["sev"].Value) : DefaultSeverity(tool);
        var file = m.Groups["file"].Value.Trim();
        var line = m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out var l) ? l : 0;
        var column = m.Groups["col"].Success && int.TryParse(m.Groups["col"].Value, out var c) ? c : 0;
        var code = m.Groups["code"].Success ? m.Groups["code"].Value.Trim() : null;
        var message = m.Groups["msg"].Value.Trim();

        return new Diagnostic(file, line, column, severity, string.IsNullOrEmpty(code) ? null : code, message);
    }

    private static string NormalizeSeverity(string severity) => severity.ToLowerInvariant() switch
    {
        "warning" => "warning",
        "information" or "note" => "info",
        _ => "error",
    };

    private static string DefaultSeverity(DiagnosticTool tool) => tool switch
    {
        DiagnosticTool.Ruff => "warning",
        DiagnosticTool.GoVet => "warning",
        _ => "error",
    };

    private static bool TryParseEslintJson(string text, List<Diagnostic> diagnostics)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return false;

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            foreach (var file in doc.RootElement.EnumerateArray())
            {
                if (!file.TryGetProperty("filePath", out var filePath) || !file.TryGetProperty("messages", out var messages))
                    continue;
                var path = filePath.GetString();
                if (string.IsNullOrEmpty(path) || messages.ValueKind != JsonValueKind.Array) continue;

                foreach (var message in messages.EnumerateArray())
                {
                    var line = message.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                    var column = message.TryGetProperty("column", out var c) ? c.GetInt32() : 0;
                    var text2 = message.TryGetProperty("message", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                    var rule = message.TryGetProperty("ruleId", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                    var severityLevel = message.TryGetProperty("severity", out var s) ? s.GetInt32() : 2;
                    diagnostics.Add(new Diagnostic(path, line, column, severityLevel >= 2 ? "error" : "warning", rule, text2));
                }
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Tail(string text, int lines)
    {
        var all = text.Split('\n');
        return all.Length <= lines ? text.TrimEnd() : string.Join('\n', all[^lines..]).TrimEnd();
    }
}
