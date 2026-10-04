using System.Text.RegularExpressions;
using ConexyAI.Contract;

namespace ConexyAI.Service;

// TEST_RUNNER: добавлено 2026-10-04 — «запусти тесты» одним инструментом. Команда выполняется в песочнице
// (как bash), но ответ модели — структурированный отчёт: сколько прошло/упало и по каждому падению имя,
// файл:строка и ожидаемое/полученное. Детект фреймворка — по файлам проекта.
public interface ITestRunnerService
{
    /// <summary>Detects the test command for a workspace (framework override wins).</summary>
    Task<TestCommandDetection> DetectCommandAsync(Guid chatId, string? path, string? framework, CancellationToken ct = default);

    /// <summary>Maps a free-form framework name to the enum (Unknown when unrecognised).</summary>
    TestFramework ResolveFramework(string? name);

    /// <summary>Parses raw test output into a structured summary.</summary>
    TestRunSummary Parse(TestFramework framework, string output, bool truncated);
}

public class ConexyTestRunnerService : ITestRunnerService
{
    private readonly IConexyWorkspaceService _workspaceService;

    public ConexyTestRunnerService(IConexyWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    public TestFramework ResolveFramework(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "pytest" or "python" or "py" => TestFramework.Pytest,
        "jest" => TestFramework.Jest,
        "vitest" => TestFramework.Vitest,
        "mocha" => TestFramework.Mocha,
        "dotnet" or "dotnet-test" or "xunit" or "nunit" or "mstest" => TestFramework.DotnetTest,
        "go" or "gotest" or "go-test" => TestFramework.GoTest,
        "cargo" or "rust" or "cargo-test" => TestFramework.CargoTest,
        "rspec" or "ruby" => TestFramework.RSpec,
        "phpunit" or "php" => TestFramework.PhpUnit,
        _ => TestFramework.Unknown,
    };

    public async Task<TestCommandDetection> DetectCommandAsync(Guid chatId, string? path, string? framework, CancellationToken ct = default)
    {
        var directory = string.IsNullOrWhiteSpace(path) ? string.Empty : path!.Trim();
        var list = await _workspaceService.ListFilesAsync(chatId, directory, ct);
        if (!list.Success)
            return new TestCommandDetection(null, TestFramework.Unknown, list.Error);

        var files = list.Files.Select(Normalize).ToList();
        bool Has(string name) => files.Any(f =>
            f.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
        bool HasExtension(string ext) => files.Any(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        var forced = ResolveFramework(framework);
        if (!string.IsNullOrWhiteSpace(framework))
        {
            var command = await CommandForFrameworkAsync(chatId, forced, files, Has, ct);
            return command is null
                ? new TestCommandDetection(null, forced, $"Could not build a '{framework}' test command; no matching files found.")
                : new TestCommandDetection(command, forced, null);
        }

        // Node — только если в package.json реально есть тестовый скрипт или тест-раннер в зависимостях.
        if (Has("package.json"))
        {
            var packageJson = await TryReadAsync(chatId, FindFile(files, "package.json"), ct);
            var (nodeFramework, nodeCommand) = ClassifyNode(packageJson, Has);
            if (nodeCommand is not null)
                return new TestCommandDetection(nodeCommand, nodeFramework, null);
        }

        if (HasExtension(".sln") || HasExtension(".csproj"))
            return new TestCommandDetection("dotnet test --nologo", TestFramework.DotnetTest, null);

        if (Has("go.mod"))
            return new TestCommandDetection("go test ./...", TestFramework.GoTest, null);

        if (Has("Cargo.toml"))
            return new TestCommandDetection("cargo test --no-fail-fast", TestFramework.CargoTest, null);

        if (IsPytestProject(files, Has))
            return new TestCommandDetection("python -m pytest -q", TestFramework.Pytest, null);

        if (Has("Gemfile"))
            return new TestCommandDetection("bundle exec rspec", TestFramework.RSpec, null);

        if (Has("composer.json"))
            return new TestCommandDetection(Has("vendor/bin/phpunit") ? "vendor/bin/phpunit" : "phpunit", TestFramework.PhpUnit, null);

        return new TestCommandDetection(null, TestFramework.Unknown,
            "Could not detect a test setup. Pass 'command' explicitly (it needs confirmation) or a 'framework' name.");
    }

    private async Task<string?> CommandForFrameworkAsync(
        Guid chatId, TestFramework framework, List<string> files, Func<string, bool> has, CancellationToken ct)
    {
        switch (framework)
        {
            case TestFramework.Pytest: return "python -m pytest -q";
            case TestFramework.DotnetTest: return "dotnet test --nologo";
            case TestFramework.GoTest: return "go test ./...";
            case TestFramework.CargoTest: return "cargo test --no-fail-fast";
            case TestFramework.RSpec: return "bundle exec rspec";
            case TestFramework.PhpUnit: return has("vendor/bin/phpunit") ? "vendor/bin/phpunit" : "phpunit";
            case TestFramework.Jest:
            case TestFramework.Vitest:
            case TestFramework.Mocha:
            {
                var runner = framework switch
                {
                    TestFramework.Jest => "jest",
                    TestFramework.Vitest => "vitest",
                    _ => "mocha",
                };
                if (has("package.json"))
                {
                    var packageJson = await TryReadAsync(chatId, FindFile(files, "package.json"), ct);
                    if (packageJson is not null && HasTestScript(packageJson))
                        return "npm test --silent";
                }
                var localBin = $"node_modules/.bin/{runner}";
                if (has(localBin))
                    return framework == TestFramework.Vitest ? $"{localBin} run" : $"{localBin} --ci";
                return framework == TestFramework.Vitest ? $"npx --no-install {runner} run" : $"npx --no-install {runner}";
            }
            default: return null;
        }
    }

    private static (TestFramework Framework, string? Command) ClassifyNode(string? packageJson, Func<string, bool> has)
    {
        var text = packageJson ?? string.Empty;
        var framework = text.Contains("vitest", StringComparison.OrdinalIgnoreCase) ? TestFramework.Vitest
            : text.Contains("jest", StringComparison.OrdinalIgnoreCase) ? TestFramework.Jest
            : text.Contains("mocha", StringComparison.OrdinalIgnoreCase) ? TestFramework.Mocha
            : TestFramework.Unknown;

        if (HasTestScript(text))
            return (framework, "npm test --silent");
        if (framework == TestFramework.Unknown)
            return (TestFramework.Unknown, null);

        var runner = framework switch { TestFramework.Vitest => "vitest", TestFramework.Jest => "jest", _ => "mocha" };
        var localBin = $"node_modules/.bin/{runner}";
        if (has(localBin))
            return (framework, framework == TestFramework.Vitest ? $"{localBin} run" : $"{localBin} --ci");
        return (framework, framework == TestFramework.Vitest ? $"npx --no-install {runner} run" : $"npx --no-install {runner}");
    }

    private static bool HasTestScript(string packageJson)
    {
        // Ищем "test" внутри блока "scripts" — без полноценного JSON-парсинга, чтобы не падать на комментариях.
        var scriptsIndex = packageJson.IndexOf("\"scripts\"", StringComparison.OrdinalIgnoreCase);
        if (scriptsIndex < 0) return false;
        var slice = packageJson[scriptsIndex..];
        var end = slice.IndexOf('}');
        if (end >= 0) slice = slice[..end];
        return slice.Contains("\"test\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPytestProject(List<string> files, Func<string, bool> has)
    {
        if (has("pytest.ini") || has("conftest.py") || has("tox.ini") || has("setup.cfg") || has("pyproject.toml"))
            return true;
        return files.Any(f =>
        {
            var name = Path.GetFileName(f);
            return name.StartsWith("test_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string? FindFile(List<string> files, string name) =>
        files.FirstOrDefault(f => f.Equals(name, StringComparison.OrdinalIgnoreCase)
            || f.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));

    private async Task<string?> TryReadAsync(Guid chatId, string? relativePath, CancellationToken ct)
    {
        if (relativePath is null) return null;
        var res = await _workspaceService.ReadFileAsync(chatId, relativePath, ct);
        return res.Success ? res.Content : null;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    public TestRunSummary Parse(TestFramework framework, string output, bool truncated) => TestOutputParser.Parse(framework, output, truncated);
}

/// <summary>
/// Regex parsers for the common test runners. Each returns counts plus a failure list with file:line and,
/// where the framework prints them, expected/actual values.
/// </summary>
internal static class TestOutputParser
{
    public static TestRunSummary Parse(TestFramework framework, string output, bool truncated)
    {
        var text = output ?? string.Empty;
        var failures = new List<TestFailure>();
        int passed = 0, failed = 0, skipped = 0;
        string? duration = null;
        var parsed = true;

        switch (framework)
        {
            case TestFramework.Pytest: ParsePytest(text, failures, ref passed, ref failed, ref skipped, ref duration); break;
            case TestFramework.Jest:
            case TestFramework.Vitest:
            case TestFramework.Mocha: ParseJest(text, failures, ref passed, ref failed, ref skipped, ref duration); break;
            case TestFramework.DotnetTest: ParseDotnet(text, failures, ref passed, ref failed, ref skipped); break;
            case TestFramework.GoTest: ParseGo(text, failures, ref passed, ref failed, ref skipped); break;
            case TestFramework.CargoTest: ParseCargo(text, failures, ref passed, ref failed, ref skipped); break;
            case TestFramework.RSpec: ParseRSpec(text, failures, ref passed, ref failed, ref skipped, ref duration); break;
            case TestFramework.PhpUnit: ParsePhpUnit(text, failures, ref passed, ref failed, ref skipped); break;
            default: parsed = false; break;
        }

        if (!parsed || passed + failed + skipped == 0)
            TryGenericCounts(text, ref passed, ref failed, ref skipped);

        return new TestRunSummary(
            framework, parsed, passed, failed, skipped, passed + failed + skipped, duration,
            failures, Tail(text, 40), truncated);
    }

    // ---- pytest ----

    private static readonly Regex PytestHeader = new(@"^_{5,}\s*(?<name>.+?)\s*_{5,}\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex PytestCount = new(@"(?<n>\d+)\s+(?<k>passed|failed|error|errors|skipped|xfailed|xpassed)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ParsePytest(string text, List<TestFailure> failures, ref int passed, ref int failed, ref int skipped, ref string? duration)
    {
        foreach (Match m in PytestCount.Matches(text))
        {
            var n = int.Parse(m.Groups["n"].Value);
            switch (m.Groups["k"].Value.ToLowerInvariant())
            {
                case "passed": case "xpassed": passed += n; break;
                case "failed": case "error": case "errors": failed += n; break;
                case "skipped": case "xfailed": skipped += n; break;
            }
        }
        var dur = Regex.Match(text, @"in (?<d>[\d.]+)s");
        if (dur.Success) duration = dur.Groups["d"].Value + "s";

        var headers = PytestHeader.Matches(text);
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            var end = i + 1 < headers.Count ? headers[i + 1].Index : text.Length;
            var block = text[header.Index..end];
            var name = header.Groups["name"].Value.Trim();
            if (name.Length == 0) continue;

            var location = Regex.Match(block, @"(?<file>[\w./\\-]+\.py):(?<line>\d+)");
            // Expected/actual — только из строки вывода pytest («E   assert 2 == 3»), а не из исходника.
            var assertion = Regex.Match(block, @"^\s*E\s+assert\s+(?<actual>.+?)\s*==\s*(?<expected>.+?)\s*$", RegexOptions.Multiline);
            failures.Add(new TestFailure(
                name,
                location.Success ? location.Groups["file"].Value : null,
                location.Success ? int.Parse(location.Groups["line"].Value) : null,
                assertion.Success ? assertion.Groups["expected"].Value.Trim() : null,
                assertion.Success ? assertion.Groups["actual"].Value.Trim() : null,
                FirstMeaningfulLine(block)));
        }

        if (failures.Count == 0)
        {
            foreach (Match m in Regex.Matches(text, @"^FAILED\s+(?<name>\S.*?)(?:\s+-\s+(?<msg>.*))?$", RegexOptions.Multiline))
            {
                var name = m.Groups["name"].Value.Trim();
                failures.Add(new TestFailure(name, null, null, null, null, m.Groups["msg"].Success ? m.Groups["msg"].Value.Trim() : null));
            }
        }
    }

    // ---- jest / vitest / mocha ----

    private static void ParseJest(string text, List<TestFailure> failures, ref int passed, ref int failed, ref int skipped, ref string? duration)
    {
        // Jest печатает "Tests: 1 failed, 2 passed, 3 total", vitest — "Tests  1 failed | 2 passed (3)".
        // Нулевые категории часто опускаются, поэтому разбираем токены, а не один жёсткий шаблон.
        var jestLine = Regex.Match(text, @"^Tests:.*$", RegexOptions.Multiline);
        var vitestLine = jestLine.Success ? Match.Empty : Regex.Match(text, @"^\s*Tests\s+.*$", RegexOptions.Multiline);
        var countsLine = jestLine.Success ? jestLine.Value : vitestLine.Success ? vitestLine.Value : null;
        if (countsLine is not null)
        {
            foreach (Match t in Regex.Matches(countsLine, @"(?<n>\d+)\s+(?<k>failed|passed|skipped|todo)"))
            {
                var n = int.Parse(t.Groups["n"].Value);
                switch (t.Groups["k"].Value)
                {
                    case "passed": passed += n; break;
                    case "failed": failed += n; break;
                    case "skipped": case "todo": skipped += n; break;
                }
            }
        }
        else
        {
            passed += MatchInt(text, @"(?<n>\d+)\s+passing");
            failed += MatchInt(text, @"(?<n>\d+)\s+failing");
            skipped += MatchInt(text, @"(?<n>\d+)\s+pending");
        }
        var dur = Regex.Match(text, @"Time:\s*(?<d>[\d.]+\s*s)", RegexOptions.IgnoreCase);
        if (dur.Success) duration = dur.Groups["d"].Value.Trim();

        // Jest/vitest mark each failure with a leading "●". The name is the first line of the block.
        var blocks = Regex.Matches(text, @"^[ \t]*●\s*(?<name>.+?)\s*$", RegexOptions.Multiline);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var end = i + 1 < blocks.Count ? blocks[i + 1].Index : text.Length;
            var body = text[block.Index..end];
            var name = CleanName(block.Groups["name"].Value);
            var expected = Regex.Match(body, @"^[ \t]*Expected:?\s*(?<v>.+?)\s*$", RegexOptions.Multiline);
            var actual = Regex.Match(body, @"^[ \t]*(?:Received|Actual):?\s*(?<v>.+?)\s*$", RegexOptions.Multiline);
            var location = Regex.Match(body, @"\((?<file>[\w./\\-]+?\.(?:ts|tsx|js|jsx|mjs|cjs)):(?<line>\d+):\d+\)");
            failures.Add(new TestFailure(
                name,
                location.Success ? location.Groups["file"].Value : null,
                location.Success ? int.Parse(location.Groups["line"].Value) : null,
                expected.Success ? expected.Groups["v"].Value.Trim() : null,
                actual.Success ? actual.Groups["v"].Value.Trim() : null,
                FirstMeaningfulLine(body)));
        }

        // Vitest: "❯ path > suite > test  N ms" and mocha's numbered failures.
        if (failures.Count == 0)
        {
            foreach (Match m in Regex.Matches(text, @"^\s*(?:FAIL|×|✕)\s+(?<file>[\w./\\-]+)\s*>\s*(?<name>.+?)\s*$", RegexOptions.Multiline))
                failures.Add(new TestFailure(CleanName(m.Groups["name"].Value), m.Groups["file"].Value, null, null, null, null));
        }
        if (failures.Count == 0)
        {
            foreach (Match m in Regex.Matches(text, @"^\s*\d+\)\s+(?<name>.+?)\s*$", RegexOptions.Multiline))
                failures.Add(new TestFailure(CleanName(m.Groups["name"].Value), null, null, null, null, null));
        }
    }

    // ---- dotnet test ----

    private static void ParseDotnet(string text, List<TestFailure> failures, ref int passed, ref int failed, ref int skipped)
    {
        var counts = Regex.Match(text, @"Failed:\s*(?<f>\d+),\s*Passed:\s*(?<p>\d+),\s*Skipped:\s*(?<s>\d+)", RegexOptions.IgnoreCase);
        if (counts.Success)
        {
            failed += Int(counts, "f"); passed += Int(counts, "p"); skipped += Int(counts, "s");
        }

        var blocks = Regex.Matches(text, @"^[ \t]*Failed\s+(?<name>(?!.*Failed:).+?)(?:\s+\[[^\]]*\])?\s*$", RegexOptions.Multiline);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var end = i + 1 < blocks.Count ? blocks[i + 1].Index : text.Length;
            var body = text[block.Index..end];
            failures.Add(new TestFailure(
                CleanName(block.Groups["name"].Value),
                MatchValue(body, @"in\s+(?<v>[^\s]+\.cs):line\s+\d+"),
                MatchIntOrNull(body, @"in\s+[^\s]+\.cs:line\s+(?<n>\d+)"),
                MatchValue(body, @"^\s*Expected:\s*(?<v>.+?)\s*$"),
                MatchValue(body, @"^\s*Actual:\s*(?<v>.+?)\s*$"),
                MatchValue(body, @"^\s*Error Message:\s*\r?\n\s*(?<v>[^\r\n]+)")));
        }
    }

    // ---- go test ----

    private static void ParseGo(string text, List<TestFailure> failures, ref int passed, ref int failed, ref int skipped)
    {
        passed += Regex.Matches(text, @"^--- PASS:", RegexOptions.Multiline).Count;
        failed += Regex.Matches(text, @"^--- FAIL:", RegexOptions.Multiline).Count;
        skipped += Regex.Matches(text, @"^--- SKIP:", RegexOptions.Multiline).Count;

        var headers = Regex.Matches(text, @"^--- FAIL:\s*(?<name>\S+)", RegexOptions.Multiline);
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            var end = i + 1 < headers.Count ? headers[i + 1].Index : text.Length;
            var body = text[header.Index..end];
            var location = Regex.Match(body, @"(?<file>[\w./\\-]+_test\.go):(?<line>\d+)");
            failures.Add(new TestFailure(
                header.Groups["name"].Value.Trim(),
                location.Success ? location.Groups["file"].Value : null,
                location.Success ? int.Parse(location.Groups["line"].Value) : null,
                MatchValue(body, @"want\s+(?<v>.+?)\s*$"),
                MatchValue(body, @"got\s+(?<v>.+?)\s*$"),
                FirstMeaningfulLine(body)));
        }
    }

    // ---- cargo test ----

    private static void ParseCargo(string text, List<TestFailure> failures, ref int passed, ref int failed, ref int skipped)
    {
        foreach (Match m in Regex.Matches(text, @"test result:\s*\w+\.\s*(?<p>\d+)\s+passed;\s*(?<f>\d+)\s+failed;\s*(?<s>\d+)\s+ignored"))
        {
            passed += Int(m, "p"); failed += Int(m, "f"); skipped += Int(m, "s");
        }

        var headers = Regex.Matches(text, @"^----\s+(?<name>.+?)\s+stdout\s+----$", RegexOptions.Multiline);
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            var end = i + 1 < headers.Count ? headers[i + 1].Index : text.Length;
            var body = text[header.Index..end];
            var location = Regex.Match(body, @"(?<file>[\w./\\-]+\.rs):(?<line>\d+)");
            failures.Add(new TestFailure(
                header.Groups["name"].Value.Trim(),
                location.Success ? location.Groups["file"].Value : null,
                location.Success ? int.Parse(location.Groups["line"].Value) : null,
                MatchValue(body, @"^\s*right:\s*(?<v>.+?)\s*$"),
                MatchValue(body, @"^\s*left:\s*(?<v>.+?)\s*$"),
                FirstMeaningfulLine(body)));
        }
    }

    // ---- rspec ----

    private static void ParseRSpec(string text, List<TestFailure> failures, ref int passed, ref int failed, ref int skipped, ref string? duration)
    {
        var counts = Regex.Match(text, @"(?<total>\d+)\s+examples?,\s*(?<f>\d+)\s+failures?");
        if (counts.Success)
        {
            var total = Int(counts, "total");
            failed += Int(counts, "f");
            passed += Math.Max(0, total - failed);
        }
        skipped += MatchInt(text, @"(?<n>\d+)\s+pending");
        var dur = Regex.Match(text, @"Finished in (?<d>[\d.]+)\s*seconds");
        if (dur.Success) duration = dur.Groups["d"].Value + "s";

        var blocks = Regex.Matches(text, @"^\s*\d+\)\s+(?<name>.+?)\s*$", RegexOptions.Multiline);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var end = i + 1 < blocks.Count ? blocks[i + 1].Index : text.Length;
            var body = text[block.Index..end];
            var location = Regex.Match(body, @"#\s+(?<file>[\w./\\-]+\.rb):(?<line>\d+)");
            failures.Add(new TestFailure(
                CleanName(block.Groups["name"].Value),
                location.Success ? location.Groups["file"].Value : null,
                location.Success ? int.Parse(location.Groups["line"].Value) : null,
                MatchValue(body, @"expected:\s*(?<v>.+?)\s*$"),
                MatchValue(body, @"got:\s*(?<v>.+?)\s*$"),
                MatchValue(body, @"^\s*(?<v>expected .+?got .+?)$")));
        }
    }

    // ---- phpunit ----

    private static void ParsePhpUnit(string text, List<TestFailure> failures, ref int passed, ref int failed, ref int skipped)
    {
        var counts = Regex.Match(text, @"Tests:\s*(?<total>\d+).*?(?:Failures:\s*(?<f>\d+))?.*?(?:Errors:\s*(?<e>\d+))?", RegexOptions.IgnoreCase);
        if (counts.Success)
        {
            var total = Int(counts, "total");
            failed += Int(counts, "f") + Int(counts, "e");
            passed += Math.Max(0, total - failed);
        }

        var blocks = Regex.Matches(text, @"^\s*\d+\)\s+(?<name>.+?)\s*$", RegexOptions.Multiline);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var end = i + 1 < blocks.Count ? blocks[i + 1].Index : text.Length;
            var body = text[block.Index..end];
            var location = Regex.Match(body, @"(?<file>[\w./\\-]+\.php):(?<line>\d+)");
            failures.Add(new TestFailure(
                CleanName(block.Groups["name"].Value),
                location.Success ? location.Groups["file"].Value : null,
                location.Success ? int.Parse(location.Groups["line"].Value) : null,
                null, null,
                FirstMeaningfulLine(body)));
        }
    }

    // ---- generic ----

    private static readonly Regex GenericCount = new(@"(?<n>\d+)\s+(?<k>passed|failed|errors?|skipped|ignored|passing|failing|pending|examples?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void TryGenericCounts(string text, ref int passed, ref int failed, ref int skipped)
    {
        foreach (Match m in GenericCount.Matches(text))
        {
            var n = int.Parse(m.Groups["n"].Value);
            switch (m.Groups["k"].Value.ToLowerInvariant())
            {
                case "passed": case "passing": passed += n; break;
                case "failed": case "failing": case "error": case "errors": failed += n; break;
                case "skipped": case "ignored": case "pending": skipped += n; break;
            }
        }
    }

    // ---- helpers ----

    private static int Int(Match match, string group) =>
        match.Groups[group].Success && int.TryParse(match.Groups[group].Value, out var value) ? value : 0;

    private static int MatchInt(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success && int.TryParse(m.Groups["n"].Value, out var value) ? value : 0;
    }

    private static int? MatchIntOrNull(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.Multiline);
        return m.Success && int.TryParse(m.Groups["n"].Value, out var value) ? value : null;
    }

    private static string? MatchValue(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.Multiline);
        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    private static string CleanName(string name) => name.Trim().TrimEnd('›', '>').Trim();

    private static string FirstMeaningfulLine(string block)
    {
        foreach (var raw in block.Split('\n'))
        {
            var line = raw.Trim().TrimStart('|', '│', 'E', '\u2502').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("--- FAIL", StringComparison.Ordinal)) continue;
            if (line.StartsWith("at ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("AssertionError", StringComparison.Ordinal) || line.StartsWith("Error", StringComparison.Ordinal)
                || line.StartsWith("expected", StringComparison.OrdinalIgnoreCase) || line.Contains("panic", StringComparison.OrdinalIgnoreCase))
                return line.Length > 300 ? line[..300] + "…" : line;
        }
        return string.Empty;
    }

    private static string Tail(string text, int lines)
    {
        var all = text.Split('\n');
        return all.Length <= lines ? text.TrimEnd() : string.Join('\n', all[^lines..]).TrimEnd();
    }
}
