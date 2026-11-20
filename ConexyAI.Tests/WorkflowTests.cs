using System.Runtime.CompilerServices;
using System.Text.Json;
using ConexyAI.Service;

// WORKFLOW: добавлено 2026-11-20
/// <summary>
/// План фан-аута: разбор задач с зависимостями, волны выполнения, отчёты зависимостей в задании,
/// предупреждения о пересечении областей записи. Логика чистая — проверяется без прогона модели.
/// </summary>
internal static class WorkflowTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("workflow: a valid plan parses with ids, dependencies and scopes", ParseAsync);
        TestRegistry.Add("workflow: invalid plans are rejected with a reason", InvalidAsync);
        TestRegistry.Add("workflow: tasks run in dependency waves", WavesAsync);
        TestRegistry.Add("workflow: a dependent task receives the reports of its prerequisites", BriefAsync);
        TestRegistry.Add("workflow: overlapping scopes are reported as a warning", OverlapAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static WorkflowPlan Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var plan = WorkflowPlan.TryParse(document.RootElement, out var error);
        Assert(plan is not null, "the plan parses: " + error);
        return plan!;
    }

    private static Task ParseAsync()
    {
        var plan = Parse("""
            [
              {"id":"backend","title":"Backend","brief":"Сделай API","scope":["ConexyAI/Service"],"preset":"default"},
              {"id":"frontend","title":"Frontend","brief":"Сделай UI","scope":["frontend/src"]},
              {"id":"qa","title":"QA","brief":"Проверь","scope":["tests"],"after":["backend","frontend"],"preset":"reviewer"}
            ]
            """);

        Assert(plan.Steps.Count == 3, "all three tasks are parsed");
        Assert(plan.Steps[2].After.SequenceEqual(["backend", "frontend"]), "dependencies are kept");
        Assert(plan.Steps[2].Preset == WorkerPreset.Reviewer, "the preset is parsed");
        Assert(plan.Steps[0].Scope.SequenceEqual(["ConexyAI/Service"]), "scopes are kept");
        return Task.CompletedTask;
    }

    private static Task InvalidAsync()
    {
        using var document = JsonDocument.Parse("""[{"id":"only","title":"One","brief":"do","scope":["src"]}]""");
        Assert(WorkflowPlan.TryParse(document.RootElement, out var tooSmall) is null
               && tooSmall!.Contains("2", StringComparison.Ordinal), "one task is rejected: " + tooSmall);

        using var missing = JsonDocument.Parse("""
            [{"id":"a","title":"A","brief":"x","scope":["s"]},{"id":"b","title":"B","brief":"y","scope":["t"],"after":["nope"]}]
            """);
        Assert(WorkflowPlan.TryParse(missing.RootElement, out var unknown) is null
               && unknown!.Contains("nope", StringComparison.Ordinal), "an unknown dependency is rejected: " + unknown);

        using var cycle = JsonDocument.Parse("""
            [{"id":"a","title":"A","brief":"x","scope":["s"],"after":["b"]},{"id":"b","title":"B","brief":"y","scope":["t"],"after":["a"]}]
            """);
        Assert(WorkflowPlan.TryParse(cycle.RootElement, out var cyclic) is null
               && cyclic!.Contains("цикл", StringComparison.Ordinal), "a cycle is rejected: " + cyclic);
        return Task.CompletedTask;
    }

    private static Task WavesAsync()
    {
        var plan = Parse("""
            [
              {"id":"api","title":"API","brief":"api","scope":["srv"]},
              {"id":"ui","title":"UI","brief":"ui","scope":["web"]},
              {"id":"qa","title":"QA","brief":"qa","scope":["tests"],"after":["api","ui"]}
            ]
            """);
        var waves = plan.Waves();
        Assert(waves.Count == 2, "two waves: independent pair, then QA");
        Assert(waves[0].SequenceEqual(["api", "ui"]), "the first wave runs the independent tasks in parallel");
        Assert(waves[1].SequenceEqual(["qa"]), "the second wave waits for both");

        Assert(TeamTaskBoard.ExecutionWaves([
            new TeamTask("a", "A"),
            new TeamTask("b", "B", DependsOn: ["a"]),
            new TeamTask("c", "C", DependsOn: ["b"]),
        ]).Count == 3, "a chain forms three waves");
        return Task.CompletedTask;
    }

    private static Task BriefAsync()
    {
        var plan = Parse("""
            [
              {"id":"api","title":"API","brief":"api","scope":["srv"]},
              {"id":"qa","title":"QA","brief":"проверь","scope":["tests"],"after":["api"]}
            ]
            """);
        var qa = plan.Steps.Single(step => step.Id == "qa");
        var brief = WorkflowPlan.BuildStepBrief(qa, new Dictionary<string, string> { ["api"] = "API готов" });
        Assert(brief.Contains("проверь", StringComparison.Ordinal), "the task brief is present");
        Assert(brief.Contains("[api] API готов", StringComparison.Ordinal), "the prerequisite report is included: " + brief);

        var longReport = new string('x', 5_000);
        var truncated = WorkflowPlan.BuildStepBrief(qa, new Dictionary<string, string> { ["api"] = longReport });
        Assert(truncated.Length < 5_000, "a huge report is truncated before it reaches the next task");
        return Task.CompletedTask;
    }

    private static Task OverlapAsync()
    {
        var plan = Parse("""
            [
              {"id":"a","title":"A","brief":"a","scope":["src/backend"]},
              {"id":"b","title":"B","brief":"b","scope":["src/backend/api"]}
            ]
            """);
        var overlaps = plan.ScopeOverlaps();
        Assert(overlaps.Count == 1 && overlaps[0].Contains("src/backend", StringComparison.Ordinal),
            "the overlap is reported: " + string.Join(", ", overlaps));
        return Task.CompletedTask;
    }
}
