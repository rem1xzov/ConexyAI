using System.Runtime.CompilerServices;
using ConexyAI.Service;

// FINAL_REPORT_FILES_ONLY: пользователь потребовал «намертво» убрать из итогового отчёта после хода
// агента непонятные команды (`cd ... && dotnet run 2>&1 | tail -6`, `exit code 0`). Отчёт теперь
// содержит только изменённые файлы, поэтому эти тесты сторожат и содержимое, и то, чего в нём быть
// не должно, даже если команды когда-нибудь снова попадут во входные данные.
internal static class FinalReportTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("final report: the appended summary lists only the changed files", FilesOnlyAsync);
        TestRegistry.Add("final report: many files are truncated with a count", TruncatesAsync);
        TestRegistry.Add("final report: command lines never reach the answer", DropsCommandLinesAsync);
        TestRegistry.Add("final report: a blank answer still gets a clean summary", BlankAnswerAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task FilesOnlyAsync()
    {
        var report = ConexyAgentRunner.BuildFinalReport(
            "Готово: обновил обработчик.",
            new[] { "frontend/src/App.tsx", "ConexyAI/Service/ConexyAgentRunner.cs" });

        Assert(report.StartsWith("Готово: обновил обработчик.", StringComparison.Ordinal),
            $"the model answer stays at the top, got '{report}'");
        Assert(report.Contains("=== Change Summary ===", StringComparison.Ordinal), "the summary header stays");
        Assert(report.Contains("Files changed:", StringComparison.Ordinal), "the file list header stays");
        Assert(report.Contains("frontend/src/App.tsx", StringComparison.Ordinal), "the first changed file is listed");
        Assert(report.Contains("ConexyAI/Service/ConexyAgentRunner.cs", StringComparison.Ordinal),
            "the second changed file is listed");
        Assert(!report.Contains("Commands executed", StringComparison.Ordinal),
            "the commands section must never come back");
        Assert(!report.Contains("&&", StringComparison.Ordinal), "no shell pipelines in the report");
        Assert(!report.Contains("2>&1", StringComparison.Ordinal), "no output redirection in the report");
        Assert(!report.Contains("exit code", StringComparison.OrdinalIgnoreCase), "no exit codes in the report");
        Assert(!report.Contains("выполнено команд", StringComparison.Ordinal), "no command counters in the report");
        Assert(!report.Contains("\n\n\n", StringComparison.Ordinal), $"blank-line runs must stay collapsed, got '{report}'");
        Assert(report.EndsWith("ConexyAI/Service/ConexyAgentRunner.cs`", StringComparison.Ordinal),
            $"the report ends with the last file and nothing else, got '{report}'");
        return Task.CompletedTask;
    }

    private static Task TruncatesAsync()
    {
        var files = Enumerable.Range(0, 25).Select(i => $"src/file{i:00}.cs").ToArray();
        var report = ConexyAgentRunner.BuildFinalReport("Готово.", files);

        Assert(report.Contains("src/file00.cs", StringComparison.Ordinal), "the list starts with the first file");
        Assert(report.Contains("src/file19.cs", StringComparison.Ordinal), "the 20th file is still listed");
        Assert(!report.Contains("src/file20.cs", StringComparison.Ordinal), "files beyond the limit are not listed");
        Assert(report.Contains("… и ещё 5.", StringComparison.Ordinal),
            $"the remainder is summarised by count, got '{report}'");
        return Task.CompletedTask;
    }

    private static Task DropsCommandLinesAsync()
    {
        // Даже если во входной коллекции когда-нибудь окажется команда, в отчёт она не попадёт:
        // отчёт умеет писать только похожие на путь строки.
        var report = ConexyAgentRunner.BuildFinalReport(
            "Проверил сборку.",
            new[]
            {
                "src/app.ts",
                "cd \"C:\\proj\" && dotnet run --project app 2>&1 | tail -6",
                "exit code 0",
                "выполнено команд: 3",
            });

        Assert(report.Contains("src/app.ts", StringComparison.Ordinal), "the real changed file is listed");
        Assert(!report.Contains("cd ", StringComparison.Ordinal), "a shell line is dropped");
        Assert(!report.Contains("&&", StringComparison.Ordinal), "pipelines are dropped");
        Assert(!report.Contains("2>&1", StringComparison.Ordinal), "redirections are dropped");
        Assert(!report.Contains("tail -6", StringComparison.Ordinal), "pipeline tails are dropped");
        Assert(!report.Contains("exit code", StringComparison.OrdinalIgnoreCase), "exit codes are dropped");
        Assert(!report.Contains("выполнено команд", StringComparison.Ordinal), "command counters are dropped");
        Assert(report.Split("- `").Length - 1 == 1, $"only the real file becomes a bullet, got '{report}'");
        return Task.CompletedTask;
    }

    private static Task BlankAnswerAsync()
    {
        var report = ConexyAgentRunner.BuildFinalReport("   ", new[] { "src/main.ts" });
        Assert(report.StartsWith("=== Change Summary ===", StringComparison.Ordinal),
            $"a blank answer starts with the summary and no leading blank line, got '{report}'");
        Assert(report.Contains("src/main.ts", StringComparison.Ordinal), "the changed file is listed");

        var plain = ConexyAgentRunner.BuildFinalReport("Просто ответ без правок", Array.Empty<string>());
        Assert(plain == "Просто ответ без правок",
            $"with no changed files the answer is returned unchanged, got '{plain}'");
        return Task.CompletedTask;
    }
}
