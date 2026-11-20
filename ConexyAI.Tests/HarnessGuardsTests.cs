using System.Runtime.CompilerServices;
using ConexyAI.Service;

// HARNESS_GUARDS: добавлено 2026-11-20 — тесты запретов пользователя, дедупа батча, детектора
// советов из ошибок, анти-лжи «инструменты отключены» и политики таймаутов.
internal static class HarnessGuardsTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("user constraints: explicit prohibitions block the right tools", UserConstraintsAsync);
        TestRegistry.Add("user constraints: read-only and no-git modes are enforced", ReadOnlyAsync);
        TestRegistry.Add("user constraints: an empty prompt imposes nothing", EmptyAsync);
        TestRegistry.Add("batch dedup: an identical idempotent call is skipped once per batch", DedupAsync);
        TestRegistry.Add("tool advice: a ready-made hint is extracted from an error", AdviceAsync);
        TestRegistry.Add("harness claims: a 'tools disabled' excuse is recognized, honest reports are not", ClaimsAsync);
        TestRegistry.Add("command timeouts: interactive, long and normal commands get 120/3600/300", TimeoutsAsync);
        TestRegistry.Add("tool arg aliases: path is restored from file/file_path/filename", AliasesAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task UserConstraintsAsync()
    {
        var constraints = UserConstraints.FromPrompt("Поправь баг, но не трогай тесты и не меняй `frontend/src/App.tsx`.");
        Assert(constraints.Any, "explicit prohibitions are parsed");

        var blockedTest = constraints.Refuse("file_write", """{"path":"tests/test_api.py","content":"x"}""");
        Assert(blockedTest is not null, "writing a test file is blocked when the user said not to touch tests: " + blockedTest);
        var blockedPath = constraints.Refuse("apply_patch", """{"patch":"--- a/frontend/src/App.tsx\n+++ b/frontend/src/App.tsx\n@@ -1 +1 @@\n-a\n+b"}""");
        Assert(blockedPath is not null, "editing a forbidden path is blocked");
        Assert(constraints.Refuse("file_write", """{"path":"src/api.py","content":"x"}""") is null,
            "an allowed path is not blocked");
        Assert(constraints.Refuse("bash", """{"command":"python -m pytest -q"}""") is null,
            "running tests is not blocked by a test-file prohibition");

        var english = UserConstraints.FromPrompt("Fix the bug, do not edit test files, do not commit.");
        Assert(english.Refuse("file_write", """{"path":"test_x.py","content":"x"}""") is not null,
            "English 'do not edit test files' blocks test writes");
        Assert(english.Refuse("bash", """{"command":"git commit -m fix"}""") is not null,
            "English 'do not commit' blocks git commit");

        // «Не меняй поведение» — это требование к смыслу, а не запрет файлов с «поведением» в имени.
        var semantics = UserConstraints.FromPrompt("Отрефактори, но не меняй поведение функции.");
        Assert(!semantics.Any, "a vague semantic prohibition imposes no path filter");
        Assert(semantics.Refuse("file_write", """{"path":"src/api.py","content":"x"}""") is null,
            "a vague prohibition must not block unrelated file writes");
        return Task.CompletedTask;
    }

    private static Task ReadOnlyAsync()
    {
        var constraints = UserConstraints.FromPrompt("Только читай, ничего не меняй: объясни, как работает модуль.");
        Assert(constraints.ReadOnly, "read-only mode is parsed");
        Assert(constraints.Refuse("file_write", """{"path":"src/a.py","content":"x"}""") is not null,
            "writes are blocked in read-only mode");
        Assert(constraints.Refuse("bash", """{"command":"cat src/a.py"}""") is null,
            "reading through bash is allowed in read-only mode");
        Assert(constraints.Refuse("bash", """{"command":"rm -rf src"}""") is not null,
            "a writing command is blocked in read-only mode");

        var noGit = UserConstraints.FromPrompt("Отрефактори, но не коммить и не пушить.");
        Assert(noGit.Refuse("bash", """{"command":"git push origin main"}""") is not null, "git push is blocked");
        Assert(noGit.Refuse("bash", """{"command":"git status --short"}""") is null, "git status is still allowed");
        return Task.CompletedTask;
    }

    private static Task EmptyAsync()
    {
        var constraints = UserConstraints.FromPrompt("Добавь endpoint и тесты к нему.");
        Assert(!constraints.Any && constraints.Refuse("file_write", """{"path":"src/x.py","content":"y"}""") is null,
            "a normal task imposes no constraints");
        Assert(!UserConstraints.FromPrompt(null).Any, "null prompt imposes nothing");
        return Task.CompletedTask;
    }

    private static Task DedupAsync()
    {
        var dedup = new BatchCallDeduplicator();
        const string first = """{"framework":"pytest"}""";
        Assert(!dedup.ShouldSkip("run_tests", first), "the first call executes");
        Assert(dedup.ShouldSkip("run_tests", first), "the identical second call in the same batch is skipped");
        Assert(!dedup.ShouldSkip("run_tests", """{"command":"python -m pytest -q"}"""),
            "a call with different arguments is not a duplicate");
        Assert(!dedup.ShouldSkip("file_write", """{"path":"a","content":"x"}"""),
            "write tools are never deduplicated");

        dedup.Reset();
        Assert(!dedup.ShouldSkip("run_tests", first), "a new batch starts clean");
        return Task.CompletedTask;
    }

    private static Task AdviceAsync()
    {
        const string shadowing =
            "AttributeError: module 'numbers' has no attribute 'Number' (consider renaming " +
            "'C:\\proj\\numbers.py' since it has the same name as the standard library module named 'numbers')";
        var advice = ToolAdviceDetector.Detect(shadowing);
        Assert(advice is not null && advice.Contains("совет", StringComparison.OrdinalIgnoreCase),
            "the ready-made advice is extracted: " + advice);

        Assert(ToolAdviceDetector.Detect("ModuleNotFoundError: No module named 'pytest'") is null,
            "plain errors without advice produce no hint");
        Assert(ToolAdviceDetector.Detect(null) is null, "null output produces no hint");
        return Task.CompletedTask;
    }

    private static Task ClaimsAsync()
    {
        Assert(HarnessClaims.ClaimsToolsDisabled("Не смог: инструменты были отключены."), "Russian claim is caught");
        Assert(HarnessClaims.ClaimsToolsDisabled("I could not continue: tools are disabled."), "English claim is caught");
        Assert(!HarnessClaims.ClaimsToolsDisabled("Пользователь отклонил выполнение команды."),
            "an honest user rejection is not an excuse about disabled tools");
        Assert(!HarnessClaims.ClaimsToolsDisabled("Нет доступа к API: ключ не настроен."),
            "an honest external blocker is not an excuse about disabled tools");
        return Task.CompletedTask;
    }

    private static Task AliasesAsync()
    {
        Assert(ToolArgumentAliases.TryRepair("file_write", """{"file_path":"a.txt","content":"x"}""", out var repaired),
            "file_path repairs the missing path");
        using (var doc = System.Text.Json.JsonDocument.Parse(repaired))
            Assert(doc.RootElement.GetProperty("path").GetString() == "a.txt", "the alias value moves into path");
        Assert(ToolArgumentAliases.TryRepair("file_read", """{"filename":"b.txt"}""", out _), "filename repairs too");
        Assert(!ToolArgumentAliases.TryRepair("grep", """{"pattern":"x"}""", out _), "non-path tools are untouched");
        Assert(!ToolArgumentAliases.TryRepair("file_write", """{"content":"x"}""", out _), "nothing to repair without an alias");
        Assert(!ToolArgumentAliases.TryRepair("file_write", """{"path":"a.txt","file":"b.txt"}""", out _), "an existing path is never overwritten");
        return Task.CompletedTask;
    }

    private static Task TimeoutsAsync()
    {
        Assert(CommandTimeoutPolicy.Resolve("ssh user@host", null) == 120, "ssh is interactive (120 s)");
        Assert(CommandTimeoutPolicy.Resolve("python", null) == 120, "a bare REPL is interactive (120 s)");
        Assert(CommandTimeoutPolicy.Resolve("git commit", null) == 120, "git commit without -m is interactive");
        Assert(CommandTimeoutPolicy.Resolve("git commit -m fix", null) == 300, "git commit with -m is not interactive");
        Assert(CommandTimeoutPolicy.Resolve("python -m pytest -q", null) == 3600, "pytest is long (3600 s)");
        Assert(CommandTimeoutPolicy.Resolve("dotnet test", null) == 3600, "dotnet test is long");
        Assert(CommandTimeoutPolicy.Resolve("pip install requests", null) == 3600, "pip install is long");
        Assert(CommandTimeoutPolicy.Resolve("git status", null) == 300, "git status is a normal command (300 s)");
        Assert(CommandTimeoutPolicy.Resolve("ssh host", 60) == 60, "an explicit timeout wins");
        Assert(CommandTimeoutPolicy.Resolve("python -m pytest -q", 9_999) == 3_600, "the maximum is 3600 s");
        Assert(CommandTimeoutPolicy.WaitedForInput("Password: ") && CommandTimeoutPolicy.WaitedForInput("EOFError: EOF when reading a line"),
            "input-waiting output is recognized");
        Assert(!CommandTimeoutPolicy.WaitedForInput("All tests passed"), "a normal output is not input-waiting");
        Assert(CommandTimeoutPolicy.TimeoutMessage("ssh host", 120).Contains("доступны", StringComparison.Ordinal),
            "the timeout message tells the model that tools remain available");
        return Task.CompletedTask;
    }
}
