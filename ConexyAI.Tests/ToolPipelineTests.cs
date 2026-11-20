using System.Runtime.CompilerServices;
using ConexyAI.Service;

// TOOL_PIPELINE: добавлено 2026-11-20
/// <summary>
/// Монотонный конвейер отказных гейтов: первый отказ побеждает, разрешить уже запрещённое нельзя,
/// а существующие стражи (цикл, экран) подключаются как гейты без правки большого цикла.
/// </summary>
internal static class ToolPipelineTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("tool pipeline: the first refusal wins and stops the chain", FirstRefusalAsync);
        TestRegistry.Add("tool pipeline: all gates passing means the call is allowed", AllPassAsync);
        TestRegistry.Add("tool pipeline: the loop guard refuses an identical repeated call", LoopGateAsync);
        TestRegistry.Add("tool pipeline: the computer-use gate refuses a no-effect click twice", ComputerGateAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private sealed class Gate(string name, string? refusal, List<string>? calls = null) : IToolGate
    {
        public string Name => name;

        public string? Refuse(string tool, string arguments)
        {
            calls?.Add(name);
            return refusal;
        }
    }

    private static Task FirstRefusalAsync()
    {
        var calls = new List<string>();
        var pipeline = new ToolGatePipeline(
            new Gate("first", null, calls),
            new Gate("second", "second says no", calls),
            new Gate("third", "third says no", calls));

        var refusal = pipeline.Refuse("bash", """{"command":"x"}""");
        Assert(refusal == "second says no", "the first non-empty refusal is returned: " + refusal);
        Assert(calls.SequenceEqual(["first", "second"]), "gates after the refusal are not consulted");
        return Task.CompletedTask;
    }

    private static Task AllPassAsync()
    {
        var pipeline = new ToolGatePipeline(new Gate("a", null), new Gate("b", null));
        Assert(pipeline.Refuse("read", "{}") is null, "when every gate passes the call is allowed");
        Assert(pipeline.Names.SequenceEqual(["a", "b"]), "gate names are exposed for diagnostics");
        return Task.CompletedTask;
    }

    private static Task LoopGateAsync()
    {
        var guard = new AgentLoopGuard();
        const string tool = "bash";
        const string args = """{"command":"dotnet build"}""";
        var gate = new LoopGuardGate(guard);

        for (var i = 0; i < AgentLoopGuard.RepeatThreshold; i++)
            guard.Record(tool, args, "same output", false);

        var refusal = gate.Refuse(tool, args);
        Assert(refusal is not null, "an identical repeated call is refused by the loop guard gate");
        return Task.CompletedTask;
    }

    private static Task ComputerGateAsync()
    {
        var guard = new ComputerUseGuard();
        const string click = """{"x":100,"y":100}""";
        var gate = new ComputerUseGate(guard);
        const string state = "screen_state:{\"frame\":\"AAAA000000000001\",\"changed\":false}";

        Assert(gate.Refuse("computer_click", click) is null, "the first no-op click is allowed");
        guard.Record("computer_click", click, state, isError: false);
        guard.Record("computer_click", click, state, isError: false);
        var refusal = gate.Refuse("computer_click", click);
        Assert(refusal is not null && refusal.Contains("не изменился", StringComparison.Ordinal),
            "a repeated no-effect click is refused through the gate: " + refusal);
        return Task.CompletedTask;
    }
}
