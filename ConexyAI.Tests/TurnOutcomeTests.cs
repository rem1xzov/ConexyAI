using System.Runtime.CompilerServices;
using ConexyAI.Contract;
using ConexyAI.Model;
using ConexyAI.Service;

// TURN_OUTCOME: добавлено 2026-11-20
/// <summary>
/// Причина завершения прогона: машинные коды для событий/логов и значение по умолчанию в интерфейсе,
/// чтобы старые тестовые двойники продолжали собираться.
/// </summary>
internal static class TurnOutcomeTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("turn outcome: every reason has a stable machine code", CodesAsync);
        TestRegistry.Add("turn outcome: the interface exposes no outcome until a run finishes", DefaultAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task CodesAsync()
    {
        Assert(new TurnResult(TurnOutcome.Completed).Code == "completed", "completed code");
        Assert(new TurnResult(TurnOutcome.ForcedFinal).Code == "forced_final", "forced_final code");
        Assert(new TurnResult(TurnOutcome.TokenBudget).Code == "token_budget", "token_budget code");
        Assert(new TurnResult(TurnOutcome.Stopped).Code == "stopped", "stopped code");
        Assert(new TurnResult(TurnOutcome.Failed).Code == "failed", "failed code");
        Assert(new TurnResult(TurnOutcome.Completed, "note").Note == "note", "the note is kept");
        return Task.CompletedTask;
    }

    private static Task DefaultAsync()
    {
        IConexyAgentRunner runner = new StubRunner();
        Assert(runner.LastOutcome is null, "before a run finishes the outcome is unknown");
        return Task.CompletedTask;
    }

    private sealed class StubRunner : IConexyAgentRunner
    {
        public Task<string> RunLoopAsync(ConexyJob job, ConversationContext context, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public string GetSystemPrompt(ConexyModelType modelType) => string.Empty;

        public Task<string> RunWorkerAsync(
            ConexyJob job, AgentIdentity identity, string brief, CancellationToken ct = default,
            bool localModel = false, LlmRoute? routeOverride = null, WorkerPreset preset = WorkerPreset.Default) =>
            Task.FromResult(string.Empty);

        public string PartialOutput => string.Empty;
    }
}
