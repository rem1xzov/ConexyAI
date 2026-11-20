using System.Runtime.CompilerServices;
using ConexyAI.Service;

// TEAM_BOARD: добавлено 2026-11-20
/// <summary>
/// Доска задач команды: зависимости (DAG), CAS по ревизии, области записи без пересечений.
/// Логика чистая — проверяется без прогона модели.
/// </summary>
internal static class TeamTaskBoardTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("team board: a valid DAG passes and duplicates or missing dependencies fail", ValidateAsync);
        TestRegistry.Add("team board: dependency cycles are reported", CycleAsync);
        TestRegistry.Add("team board: a task is claimed only when it is ready and the revision matches", ClaimAsync);
        TestRegistry.Add("team board: only the owner can complete a task, and only once", CompleteAsync);
        TestRegistry.Add("team board: overlapping write scopes are reported", ScopesAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static TeamTask Node(string id, string? owner = null, TeamTaskStatus status = TeamTaskStatus.Pending,
        IReadOnlyList<string>? dependsOn = null, IReadOnlyList<string>? scopes = null, int revision = 0) =>
        new(id, "task " + id, status, owner, dependsOn, scopes, revision);

    private static Task ValidateAsync()
    {
        var valid = new[]
        {
            Node("a"),
            Node("b", dependsOn: ["a"]),
        };
        Assert(TeamTaskBoard.Validate(valid).Count == 0, "a valid DAG has no problems");

        Assert(TeamTaskBoard.Validate([Node("a"), Node("a")]).Any(p => p.Contains("дубликаты", StringComparison.Ordinal)),
            "duplicate ids are reported");
        Assert(TeamTaskBoard.Validate([Node("a", dependsOn: ["missing"])]).Any(p => p.Contains("не существует", StringComparison.Ordinal)),
            "a missing dependency is reported");
        Assert(TeamTaskBoard.Validate([Node("a", owner: "x", status: TeamTaskStatus.InProgress)]).Count == 0,
            "an in-progress task with an owner is valid");
        Assert(TeamTaskBoard.Validate([Node("a", status: TeamTaskStatus.InProgress)]).Any(p => p.Contains("владелец", StringComparison.Ordinal)),
            "an in-progress task without an owner is invalid");
        return Task.CompletedTask;
    }

    private static Task CycleAsync()
    {
        var cycle = new[]
        {
            Node("a", dependsOn: ["b"]),
            Node("b", dependsOn: ["a"]),
        };
        Assert(TeamTaskBoard.Validate(cycle).Any(p => p.Contains("цикл", StringComparison.Ordinal)),
            "a dependency cycle is reported");
        return Task.CompletedTask;
    }

    private static Task ClaimAsync()
    {
        var blocked = new[] { Node("a"), Node("b", dependsOn: ["a"]) };
        Assert(TeamTaskBoard.Claim(blocked, "b", "agent-1", expectedRevision: 0) is null,
            "a task with an unfinished dependency cannot be claimed");

        var afterA = TeamTaskBoard.Complete(blocked, "a", "agent-1", expectedRevision: 0);
        Assert(afterA is null, "an unclaimed task cannot be completed");

        var claimedA = TeamTaskBoard.Claim(blocked, "a", "agent-1", expectedRevision: 0)!;
        Assert(claimedA.Status == TeamTaskStatus.InProgress && claimedA.Owner == "agent-1" && claimedA.Revision == 1,
            "claiming moves the task to in-progress and bumps the revision");
        var afterClaim = new[] { claimedA, Node("b", dependsOn: ["a"]) };
        Assert(TeamTaskBoard.Claim(afterClaim, "a", "agent-2", expectedRevision: 0) is null,
            "a stale revision cannot be claimed again");
        Assert(TeamTaskBoard.Claim(afterClaim, "b", "agent-2", expectedRevision: 0) is null,
            "a dependency claimed but not completed still blocks the next task");
        return Task.CompletedTask;
    }

    private static Task CompleteAsync()
    {
        var tasks = new[] { Node("a", owner: "agent-1", status: TeamTaskStatus.InProgress, revision: 1) };
        Assert(TeamTaskBoard.Complete(tasks, "a", "agent-2", expectedRevision: 1) is null, "a stranger cannot complete the task");
        Assert(TeamTaskBoard.Complete(tasks, "a", "agent-1", expectedRevision: 0) is null, "a stale revision cannot complete the task");

        var done = TeamTaskBoard.Complete(tasks, "a", "agent-1", expectedRevision: 1);
        Assert(done is not null && done.Status == TeamTaskStatus.Completed && done.Revision == 2,
            "the owner completes the task and bumps the revision");
        return Task.CompletedTask;
    }

    private static Task ScopesAsync()
    {
        var tasks = new[]
        {
            Node("a", scopes: ["src/backend"]),
            Node("b", scopes: ["src/backend/api"]),
            Node("c", scopes: ["src/frontend"]),
        };
        var overlaps = TeamTaskBoard.ScopeOverlaps(tasks);
        Assert(overlaps.Count == 1 && overlaps[0].Contains("a:src/backend", StringComparison.Ordinal),
            "only the intersecting pair is reported: " + string.Join(", ", overlaps));
        return Task.CompletedTask;
    }
}
