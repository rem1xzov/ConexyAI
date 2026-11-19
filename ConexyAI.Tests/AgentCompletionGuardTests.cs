using System.Runtime.CompilerServices;
using ConexyAI.Service;

// COMPLETION_GUARD: добавлено 2026-11-19
/// <summary>
/// Страж границы завершения хода: неисправленная ошибка команды и серия идентичных выводов.
/// Логика чистая (без БД и сети), поэтому проверяется напрямую.
/// </summary>
internal static class AgentCompletionGuardTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("completion guard: a failed command blocks the finish with its own output", FailedCommandAsync);
        TestRegistry.Add("completion guard: a later success of the same tool clears the failure", SuccessClearsAsync);
        TestRegistry.Add("completion guard: a success of another tool does not clear the failure", OtherToolDoesNotClearAsync);
        TestRegistry.Add("completion guard: a tool error that never ran a command is not evidence", ToolErrorOnlyAsync);
        TestRegistry.Add("completion guard: identical output three times in a row is no progress", RepeatedOutputAsync);
        TestRegistry.Add("completion guard: changed output or another call resets the repeat series", RepeatResetsAsync);
        TestRegistry.Add("completion guard: volatile ids and timings do not hide a repeat", VolatileOutputAsync);
        TestRegistry.Add("completion guard: evidence output is bounded", EvidenceBoundedAsync);
        TestRegistry.Add("completion guard: the prompt demands a fix, the escalation demands a new approach", PromptTextAsync);
        TestRegistry.Add("completion guard: the machine note exists only while evidence exists", NoteTextAsync);
    }

    private static Task FailedCommandAsync()
    {
        var guard = new AgentCompletionGuard();
        guard.Record("bash", """{"command":"dotnet build"}""", "error CS1002: ; expected", isError: true, commandExecuted: true);

        var evidence = guard.Inspect();
        Assert(evidence.Count == 1, "one item expected, got " + evidence.Count);
        Assert(evidence[0].Kind == CompletionEvidenceKind.FailedCommand, "kind must be FailedCommand");
        Assert(evidence[0].Tool == "bash", "tool must be bash");
        Assert(evidence[0].Detail.Contains("CS1002", StringComparison.Ordinal), "the real output must be quoted");
        Assert(evidence[0].Key.StartsWith("failed:", StringComparison.Ordinal), "the key identifies the failure: " + evidence[0].Key);
        return Task.CompletedTask;
    }

    private static Task SuccessClearsAsync()
    {
        var guard = new AgentCompletionGuard();
        guard.Record("bash", """{"command":"dotnet build"}""", "error CS1002", isError: true, commandExecuted: true);
        guard.Record("bash", """{"command":"dotnet build"}""", "Build succeeded", isError: false, commandExecuted: true);

        Assert(guard.Inspect().Count == 0, "a fixed command must clear the failure");
        return Task.CompletedTask;
    }

    private static Task OtherToolDoesNotClearAsync()
    {
        var guard = new AgentCompletionGuard();
        guard.Record("bash", """{"command":"dotnet build"}""", "error CS1002", isError: true, commandExecuted: true);
        guard.Record("file_read", """{"path":"Program.cs"}""", "class Program {}", isError: false, commandExecuted: false);

        var evidence = guard.Inspect();
        Assert(evidence.Count == 1 && evidence[0].Kind == CompletionEvidenceKind.FailedCommand,
            "reading a file does not fix a failed build");
        return Task.CompletedTask;
    }

    private static Task ToolErrorOnlyAsync()
    {
        var guard = new AgentCompletionGuard();
        guard.Record("file_write", """{"path":"a.txt"}""", "edit refused by policy", isError: true, commandExecuted: false);

        Assert(guard.Inspect().Count == 0, "a refusal is not a failed command");
        return Task.CompletedTask;
    }

    private static Task RepeatedOutputAsync()
    {
        var guard = new AgentCompletionGuard();
        const string args = """{"command":"tail app.log"}""";
        guard.Record("bash", args, "waiting for server", isError: false, commandExecuted: true);
        guard.Record("bash", args, "waiting for server", isError: false, commandExecuted: true);
        guard.Record("bash", args, "waiting for server", isError: false, commandExecuted: true);

        var evidence = guard.Inspect();
        Assert(evidence.Count == 1, "one item expected, got " + evidence.Count);
        Assert(evidence[0].Kind == CompletionEvidenceKind.RepeatedOutput, "kind must be RepeatedOutput");
        Assert(evidence[0].Summary.Contains("3", StringComparison.Ordinal), "the count must be named: " + evidence[0].Summary);
        return Task.CompletedTask;
    }

    private static Task RepeatResetsAsync()
    {
        var changed = new AgentCompletionGuard();
        const string args = """{"command":"tail app.log"}""";
        changed.Record("bash", args, "waiting for server", isError: false, commandExecuted: true);
        changed.Record("bash", args, "waiting for server", isError: false, commandExecuted: true);
        changed.Record("bash", args, "server started on 3000", isError: false, commandExecuted: true);
        Assert(changed.Inspect().Count == 0, "new output is new information, not a loop");

        var otherCall = new AgentCompletionGuard();
        otherCall.Record("bash", args, "waiting for server", isError: false, commandExecuted: true);
        otherCall.Record("bash", args, "waiting for server", isError: false, commandExecuted: true);
        otherCall.Record("bash", """{"command":"ls"}""", "waiting for server", isError: false, commandExecuted: true);
        Assert(otherCall.Inspect().Count == 0, "a different call breaks the series");
        return Task.CompletedTask;
    }

    private static Task VolatileOutputAsync()
    {
        var guard = new AgentCompletionGuard();
        const string args = """{"command":"tail app.log"}""";
        guard.Record("bash", args,
            "2026-11-19T10:00:01Z request 3f2504e0-4f89-11d3-9a0c-0305e82c3301 pending",
            isError: false, commandExecuted: true);
        guard.Record("bash", args,
            "2026-11-19T10:00:02Z request f47ac10b-58cc-4372-a567-0e02b2c3d479 pending",
            isError: false, commandExecuted: true);
        guard.Record("bash", args,
            "2026-11-19T10:00:03Z request 6ba7b810-9dad-11d1-80b4-00c04fd430c8 pending",
            isError: false, commandExecuted: true);

        Assert(guard.Inspect().Count == 1, "timings and ids must be normalized before comparing outputs");
        return Task.CompletedTask;
    }

    private static Task EvidenceBoundedAsync()
    {
        var guard = new AgentCompletionGuard();
        var huge = new string('x', 5000) + "THE TAIL";
        guard.Record("bash", """{"command":"dotnet build"}""", huge, isError: true, commandExecuted: true);

        var detail = guard.Inspect()[0].Detail;
        Assert(detail.Length <= AgentCompletionGuard.MaxEvidenceChars + 1, "the evidence must be bounded: " + detail.Length);
        Assert(detail.StartsWith('…'), "the omitted prefix must be marked");
        Assert(detail.EndsWith("THE TAIL", StringComparison.Ordinal), "the tail is what matters");
        return Task.CompletedTask;
    }

    private static Task PromptTextAsync()
    {
        var guard = new AgentCompletionGuard();
        guard.Record("bash", """{"command":"dotnet build"}""", "error CS1002", isError: true, commandExecuted: true);
        var evidence = guard.Inspect();

        var first = AgentCompletionGuard.BuildPrompt(evidence, escalated: false, attempt: 1, max: 2, toolsReopened: false);
        Assert(first.Contains("[Completion Guard]", StringComparison.Ordinal), "the first prompt must be labeled");
        Assert(first.Contains("bash", StringComparison.Ordinal), "the prompt names the failing tool");
        Assert(first.Contains("error CS1002", StringComparison.Ordinal), "the prompt quotes the evidence");
        Assert(first.Contains("Не заканчивай ход", StringComparison.Ordinal), "the prompt forbids finishing");
        Assert(!first.Contains("Инструменты снова доступны", StringComparison.Ordinal), "tools were not reopened");

        var escalated = AgentCompletionGuard.BuildPrompt(evidence, escalated: true, attempt: 2, max: 2, toolsReopened: true);
        Assert(escalated.Contains("Escalated", StringComparison.Ordinal), "the escalation must be labeled");
        Assert(escalated.Contains("Смени подход", StringComparison.Ordinal), "the escalation demands a different approach");
        Assert(escalated.Contains("Инструменты снова доступны", StringComparison.Ordinal), "a reopened turn says so");
        return Task.CompletedTask;
    }

    private static Task NoteTextAsync()
    {
        Assert(AgentCompletionGuard.BuildNote([]) is null, "no evidence means no note");

        var guard = new AgentCompletionGuard();
        guard.Record("bash", """{"command":"dotnet build"}""", "error CS1002", isError: true, commandExecuted: true);
        var note = AgentCompletionGuard.BuildNote(guard.Inspect());
        Assert(note is not null, "the note must exist while evidence does");
        Assert(note!.Contains("Проверено системой", StringComparison.Ordinal), "the note is system-authored: " + note);
        Assert(note.Contains("не подтверждённый", StringComparison.Ordinal), "the note denies a confirmed result");
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);
}
