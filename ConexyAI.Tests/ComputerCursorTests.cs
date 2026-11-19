using System.Runtime.CompilerServices;
using ConexyAI.Service;

// AGENT_CURSOR: добавлено 2026-11-19
/// <summary>
/// Координируемые курсоры: стабильные цвета агентов, реестр «где кто действует» и аренда ввода —
/// на одном ПК в один момент действует только один агент.
/// </summary>
internal static class ComputerCursorTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("agent cursors: colors are stable and the lead has its own", ColorsAsync);
        TestRegistry.Add("agent cursors: the registry keeps the latest action of every agent", RegistryAsync);
        TestRegistry.Add("agent cursors: input is leased to one agent, others are refused with the owner role", LeaseAsync);
        TestRegistry.Add("agent cursors: an expired lease lets another agent act", LeaseExpiryAsync);
        TestRegistry.Add("agent cursors: coordinates are read from the action arguments", ArgsAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task ColorsAsync()
    {
        Assert(AgentCursorColors.For("lead") == AgentCursorColors.Lead, "the lead has its fixed color");
        Assert(AgentCursorColors.For(null) == AgentCursorColors.Lead, "an unknown agent falls back to the lead color");
        Assert(AgentCursorColors.For("agent-2") == AgentCursorColors.For("agent-2"), "the color is stable between calls");
        Assert(AgentCursorColors.For("backend").StartsWith("#", StringComparison.Ordinal), "colors are hex strings");
        return Task.CompletedTask;
    }

    private static Task RegistryAsync()
    {
        var registry = new ComputerCursorRegistry();
        var taskId = Guid.NewGuid();
        registry.Update(new AgentCursorState(taskId.ToString(), "agent-2", "backend", "#5FD7AF", "computer_click", 10, 20, null, null, true, DateTime.UtcNow));
        registry.Update(new AgentCursorState(taskId.ToString(), "lead", "lead", AgentCursorColors.Lead, "computer_click", 300, 400, null, null, false, DateTime.UtcNow));

        var snapshot = registry.Snapshot(taskId);
        Assert(snapshot.Count == 2, "both agents are remembered");
        Assert(snapshot[0].AgentId == "lead", "the lead comes first");
        Assert(snapshot[1].X == 10 && snapshot[1].Y == 20, "helper coordinates are kept");

        registry.Update(new AgentCursorState(taskId.ToString(), "agent-2", "backend", "#5FD7AF", "computer_drag", 11, 21, 50, 60, true, DateTime.UtcNow));
        Assert(registry.Snapshot(taskId).Single(state => state.AgentId == "agent-2").ToX == 50, "the latest action replaces the previous one");

        registry.Clear(taskId);
        Assert(registry.Snapshot(taskId).Count == 0, "a finished run leaves no cursors");
        return Task.CompletedTask;
    }

    private static Task LeaseAsync()
    {
        var lease = new ComputerInputLease();
        var userId = Guid.NewGuid();

        Assert(lease.TryAcquire(userId, "worker-1", "backend", out var owner), "the first agent gets the input");
        Assert(owner.Length == 0, "a successful acquire has no owner conflict");
        Assert(lease.TryAcquire(userId, "worker-1", "backend", out _), "the holder may re-enter the lease");

        Assert(!lease.TryAcquire(userId, "worker-2", "frontend", out owner), "a second agent is refused");
        Assert(owner == "backend", $"the refusal names the holder role, got '{owner}'");

        lease.Release(userId, "worker-2");
        Assert(!lease.TryAcquire(userId, "worker-2", "frontend", out _), "a stranger's release changes nothing");

        lease.Release(userId, "worker-1");
        Assert(lease.TryAcquire(userId, "worker-2", "frontend", out _), "after the release the next agent gets the input");
        return Task.CompletedTask;
    }

    private static Task LeaseExpiryAsync()
    {
        var lease = new ComputerInputLease(TimeSpan.FromMilliseconds(60));
        var userId = Guid.NewGuid();
        Assert(lease.TryAcquire(userId, "worker-1", "backend", out _), "the first agent gets the input");
        Thread.Sleep(120);
        Assert(lease.TryAcquire(userId, "worker-2", "frontend", out _), "an expired lease must not block the screen forever");
        return Task.CompletedTask;
    }

    private static Task ArgsAsync()
    {
        var (x, y, toX, toY) = CursorArgs.Parse("""{"x":120,"y":80,"to_x":300,"to_y":240}""");
        Assert(x == 120 && y == 80 && toX == 300 && toY == 240, "coordinates and drag target are parsed");

        var (_, _, emptyToX, _) = CursorArgs.Parse("""{"keys":"ctrl+c"}""");
        Assert(emptyToX is null, "missing coordinates stay null");

        var (nullX, nullY, _, _) = CursorArgs.Parse("not json");
        Assert(nullX is null && nullY is null, "broken arguments do not throw");
        return Task.CompletedTask;
    }
}
