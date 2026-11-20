using System.Runtime.CompilerServices;
using ConexyAI.Service;

// REPEAT_REMINDER: добавлено 2026-11-20
/// <summary>
/// Мягкая ступень защиты от повторов: один и тот же вызов с каноничными аргументами получает
/// подсказку на порогах 3/5/8, но не блокируется. Жёсткий отказ остаётся за AgentLoopGuard.
/// </summary>
internal static class RepeatToolReminderTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("repeat reminder: canonical arguments ignore key order and whitespace", CanonicalizeAsync);
        TestRegistry.Add("repeat reminder: hints fire once at thresholds 3, 5 and 8", ThresholdsAsync);
        TestRegistry.Add("repeat reminder: different arguments and service tools are not counted", DistinctAsync);
        TestRegistry.Add("repeat reminder: a repeated verification warns at the second run", VerificationAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task CanonicalizeAsync()
    {
        var a = RepeatToolReminder.Canonicalize("""{"b":2,"a":{"y":1,"x":[2,1]}}""");
        var b = RepeatToolReminder.Canonicalize("{ \"a\": { \"x\": [2, 1], \"y\": 1 }, \"b\": 2 }");
        Assert(a == b, $"the same call in a different key order/whitespace must canonicalize equally: '{a}' vs '{b}'");

        Assert(RepeatToolReminder.Canonicalize("not json") == "not json", "invalid JSON falls back to the trimmed text");
        Assert(RepeatToolReminder.Canonicalize(null) == string.Empty, "null arguments canonicalize to empty");
        return Task.CompletedTask;
    }

    private static Task ThresholdsAsync()
    {
        var reminder = new RepeatToolReminder();
        const string tool = "grep";
        const string args = """{"pattern":"Widget","path":"src"}""";

        Assert(reminder.Record(tool, args) is null, "the first call is silent");
        Assert(reminder.Record(tool, """{ "path":"src", "pattern":"Widget" }""") is null,
            "a different key order is still the same call; the second call is silent");

        var third = reminder.Record(tool, args);
        Assert(third is not null && third.Contains("[Repeat]", StringComparison.Ordinal), "the third call warns");

        Assert(reminder.Record(tool, args) is null, "the warning is never repeated at the same threshold");
        var fifth = reminder.Record(tool, args);
        Assert(fifth is not null && fifth.Contains("известен", StringComparison.Ordinal), "the fifth call demands a new hypothesis");
        reminder.Record(tool, args);
        reminder.Record(tool, args);
        var eighth = reminder.Record(tool, args);
        Assert(eighth is not null && eighth.Contains("Прекрати повтор", StringComparison.Ordinal), "the eighth call stops the loop");
        Assert(reminder.Record(tool, args) is null, "no more hints after the last threshold");
        return Task.CompletedTask;
    }

    private static Task DistinctAsync()
    {
        var reminder = new RepeatToolReminder();
        reminder.Record("bash", """{"command":"dotnet build"}""");
        reminder.Record("bash", """{"command":"dotnet build"}""");
        Assert(reminder.Record("bash", """{"command":"dotnet test"}""") is null, "a different argument is a different action");

        for (var i = 0; i < 5; i++)
            Assert(reminder.Record("todo_write", "{}") is null, "service tools like todo_write are never reminded");

        for (var i = 0; i < 4; i++)
            reminder.Record("computer_screen", "{}");
        Assert(reminder.Record("computer_screen", "{}") is null, "screen observation is not a repeated action");
        return Task.CompletedTask;
    }

    private static Task VerificationAsync()
    {
        var reminder = new RepeatToolReminder();
        const string args = """{"framework":"pytest","command":"python -m pytest -q"}""";
        Assert(reminder.Record("run_tests", args) is null, "the first green test run is silent");
        var second = reminder.Record("run_tests", args);
        Assert(second is not null && second.Contains("проверка", StringComparison.Ordinal),
            "the second identical test run is called out: " + second);
        Assert(reminder.Record("run_tests", args) is null, "the verification hint fires once per threshold");

        var bash = new RepeatToolReminder();
        const string command = """{"command":"dotnet test --nologo"}""";
        Assert(bash.Record("bash", command) is null, "the first build/test command through bash is silent");
        Assert(bash.Record("bash", command) is not null, "the same test command through bash warns at the second run");
        Assert(bash.Record("bash", """{"command":"dotnet build"}""") is null,
            "a build (not a test) command keeps the normal threshold");

        var grep = new RepeatToolReminder();
        grep.Record("grep", """{"pattern":"x"}""");
        Assert(grep.Record("grep", """{"pattern":"x"}""") is null, "non-verification tools keep the 3-call threshold");
        return Task.CompletedTask;
    }
}
