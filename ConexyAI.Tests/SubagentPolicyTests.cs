using System.Runtime.CompilerServices;
using ConexyAI.Contract;
using ConexyAI.Model;
using ConexyAI.Service;

// SUBAGENT_AUTONOMY: добавлено 2026-11-19
/// <summary>
/// Политика самостоятельного запуска помощников: право даёт серверная возможность, а не клиентский
/// переключатель /team; помощник не может плодить помощников (глубина 1).
/// </summary>
internal static class SubagentPolicyTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("subagents: the lead spawns helpers by itself even with /team off", SpawnWithoutToggleAsync);
        TestRegistry.Add("subagents: only the coder lead may spawn; helpers cannot recurse", OnlyLeadAsync);
        TestRegistry.Add("subagents: team tools follow the same capability", TeamToolsAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static ConexyJob Job(bool orchestra, bool canSpawn) => new(
        TaskId: Guid.NewGuid(),
        ChatId: Guid.NewGuid(),
        UserId: Guid.NewGuid(),
        ModelType: ConexyModelType.ConexyCoder,
        Prompt: "тяжёлая задача",
        Orchestra: orchestra,
        CanSpawnAgents: canSpawn);

    private static Task SpawnWithoutToggleAsync()
    {
        var autonomous = Job(orchestra: false, canSpawn: true);
        Assert(SubagentPolicy.SpawnAllowed(autonomous, "coder"),
            "the lead must be able to call helpers on its own decision, not only with /team on");

        var forced = Job(orchestra: true, canSpawn: false);
        Assert(SubagentPolicy.SpawnAllowed(forced, "coder"), "the explicit /team switch still allows spawning");

        var neither = Job(orchestra: false, canSpawn: false);
        Assert(!SubagentPolicy.SpawnAllowed(neither, "coder"), "without the capability and without /team nothing is allowed");
        return Task.CompletedTask;
    }

    private static Task OnlyLeadAsync()
    {
        var autonomous = Job(orchestra: false, canSpawn: true);
        Assert(!SubagentPolicy.SpawnAllowed(autonomous, "worker"), "a helper must not spawn more helpers");
        Assert(!SubagentPolicy.SpawnAllowed(autonomous, "cowork"), "cowork has no team tools");
        Assert(!SubagentPolicy.AgentsStatusAllowed(autonomous, "worker"), "only the lead inspects the team");
        return Task.CompletedTask;
    }

    private static Task TeamToolsAsync()
    {
        var autonomous = Job(orchestra: false, canSpawn: true);
        Assert(SubagentPolicy.TeamMessageAllowed(autonomous, "coder"), "the lead may message the team");
        Assert(SubagentPolicy.TeamMessageAllowed(autonomous, "worker"), "helpers may reply to the team");
        Assert(!SubagentPolicy.TeamMessageAllowed(Job(orchestra: false, canSpawn: false), "worker"),
            "without the capability team messages are unavailable");
        return Task.CompletedTask;
    }
}
