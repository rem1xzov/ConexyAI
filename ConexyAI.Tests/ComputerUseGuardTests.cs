using System.Runtime.CompilerServices;
using System.Text.Json;
using ConexyAI.Service;

// COMPUTER_USE: добавлено 2026-11-19
/// <summary>
/// Защита computer use от циклов по наблюдаемому состоянию кадра: клик без изменения экрана,
/// застывший кадр, повтор одной и той же цели. Проверяется на разборе `screen_state:` и на страже.
/// </summary>
internal static class ComputerUseGuardTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("computer use: the state line is parsed from the tool output", ParseAsync);
        TestRegistry.Add("computer use: a click that changes nothing twice is refused once", NoEffectAsync);
        TestRegistry.Add("computer use: a changed frame clears the no-effect counter", ChangedClearsAsync);
        TestRegistry.Add("computer use: a frozen screen is refused even by different targets", FrozenAsync);
        TestRegistry.Add("computer use: observation and wait never refuse, and only real changes are progress", ProgressAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static string Output(string frame, bool? changed) =>
        "IMAGE_BASE64:image/png:AAAA\nnote\nscreen_state:" + JsonSerializer.Serialize(new { frame, changed, shot = 1 });

    private static Task ParseAsync()
    {
        var state = ScreenStateParser.Parse(Output("0123456789ABCDEF", false));
        Assert(state is not null, "the structured line must parse");
        Assert(state!.Frame == "0123456789ABCDEF" && state.Changed == false, "frame and changed must be read");
        Assert(ScreenStateParser.Parse("no state here") is null, "missing state parses to null");
        Assert(ScreenStateParser.Parse("screen_state:{broken") is null, "broken json parses to null");
        return Task.CompletedTask;
    }

    private static Task NoEffectAsync()
    {
        var guard = new ComputerUseGuard();
        const string click = """{"x":120,"y":80}""";
        guard.Record("computer_click", click, Output("AAAA000000000001", false), isError: false);
        Assert(guard.Refuse("computer_click", click) is null, "one no-op click is not a loop yet");

        guard.Record("computer_click", click, Output("AAAA000000000001", false), isError: false);
        var refusal = guard.Refuse("computer_click", click);
        Assert(refusal is not null && refusal.Contains("не изменился", StringComparison.Ordinal),
            "the second no-op click must produce an actionable refusal: " + refusal);
        Assert(guard.Refuse("computer_click", click) is null, "the refusal is one-shot and resets the counters");
        return Task.CompletedTask;
    }

    private static Task ChangedClearsAsync()
    {
        var guard = new ComputerUseGuard();
        const string click = """{"x":120,"y":80}""";
        guard.Record("computer_click", click, Output("AAAA000000000001", false), isError: false);
        guard.Record("computer_click", click, Output("BBBB000000000002", true), isError: false);
        Assert(guard.LastActionChanged, "a changed frame counts as progress");
        Assert(guard.Refuse("computer_click", click) is null,
            "an effective click clears the no-effect counter, so the repeat is allowed");
        return Task.CompletedTask;
    }

    private static Task FrozenAsync()
    {
        var guard = new ComputerUseGuard();
        const string same = "0123456789ABCDEF";
        guard.Record("computer_screen", "{}", Output(same, null), isError: false);
        guard.Record("computer_click", """{"x":10,"y":10}""", Output(same, false), isError: false);
        guard.Record("computer_click", """{"x":400,"y":300}""", Output(same, false), isError: false);

        var refusal = guard.Refuse("computer_click", """{"x":400,"y":300}""");
        Assert(refusal is not null && refusal.Contains("не меняется", StringComparison.Ordinal),
            "a frozen frame must be reported even for different targets: " + refusal);
        return Task.CompletedTask;
    }

    private static Task ProgressAsync()
    {
        var guard = new ComputerUseGuard();
        guard.Record("computer_screen", "{}", Output("1111111111111111", null), isError: false);
        Assert(guard.Refuse("computer_screen", "{}") is null, "observation is never refused");
        Assert(guard.Refuse("computer_wait", """{"ms":500}""") is null, "waiting is never refused");

        guard.Record("computer_click", """{"x":50,"y":50}""", Output("2222222222222222", true), isError: false);
        Assert(guard.LastActionChanged, "a changed frame counts as progress");

        guard.Record("computer_click", """{"x":50,"y":50}""", Output("2222222222222222", false), isError: false);
        Assert(!guard.LastActionChanged, "a no-op click is not progress");

        // Аргументы не JSON — ключ берётся по тексту, защита не падает.
        guard.Record("computer_type", "hello", Output("3333333333333333", false), isError: false);
        Assert(guard.Refuse("computer_type", "hello") is null, "a single no-effect typing is not refused");
        return Task.CompletedTask;
    }
}
