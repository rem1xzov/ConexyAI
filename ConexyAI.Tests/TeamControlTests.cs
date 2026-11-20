using System.Runtime.CompilerServices;
using ConexyAI.Service;

// TEAM_CONTROL: добавлено 2026-11-20
/// <summary>
/// Управление командой помощников: стабильные имена, поиск роли для точечной остановки и отказ с
/// перечнем доступных ролей. Логика чистая — проверяется без прогона модели.
/// </summary>
internal static class TeamControlTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("team control: helper names are stable and human-readable", NamesAsync);
        TestRegistry.Add("team control: a role is found exactly, then case-insensitively", ResolveAsync);
        TestRegistry.Add("team control: an unknown role is refused with the list of real roles", RefusalAsync);
        TestRegistry.Add("team control: the status line names the role, its name and the state", StatusAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task NamesAsync()
    {
        Assert(TeamControl.NameFor(0) == "worker-1", "the first helper is worker-1");
        Assert(TeamControl.NameFor(2) == "worker-3", "names follow the order in the team");
        return Task.CompletedTask;
    }

    private static Task ResolveAsync()
    {
        string[] roles = ["backend", "Frontend", "qa"];
        Assert(TeamControl.ResolveRole(roles, "backend") == "backend", "an exact role matches");
        Assert(TeamControl.ResolveRole(roles, "  frontend  ") == "Frontend", "case and spaces are ignored");
        Assert(TeamControl.ResolveRole(roles, "missing") is null, "an unknown role does not match");
        Assert(TeamControl.ResolveRole(roles, "") is null, "an empty role does not match");
        Assert(TeamControl.ResolveRole(roles, null) is null, "null does not match");
        return Task.CompletedTask;
    }

    private static Task RefusalAsync()
    {
        var refusal = TeamControl.Refusal(["backend", "qa"], "design");
        Assert(refusal.Contains("design", StringComparison.Ordinal), "the refusal names the requested role");
        Assert(refusal.Contains("backend", StringComparison.Ordinal) && refusal.Contains("qa", StringComparison.Ordinal),
            "the refusal lists the roles that can be stopped: " + refusal);
        Assert(TeamControl.Refusal([], "design").Contains("команда пуста", StringComparison.Ordinal),
            "an empty team says so");
        return Task.CompletedTask;
    }

    private static Task StatusAsync()
    {
        var line = TeamControl.StatusLine("backend", "worker-2", "работает");
        Assert(line.Contains("backend", StringComparison.Ordinal) && line.Contains("worker-2", StringComparison.Ordinal)
               && line.Contains("работает", StringComparison.Ordinal),
            "the status line has the role, the stable name and the state: " + line);
        return Task.CompletedTask;
    }
}
