using System.Runtime.CompilerServices;
using ConexyAI.Service;

// WORKER_PRESETS: добавлено 2026-11-20
/// <summary>
/// Пресеты помощников: роль задаёт не только текст задания, но и набор инструментов. Ресёрчер не может
/// менять файлы и запускать команды, ревьюер может читать и гонять тесты, но не правит код.
/// </summary>
internal static class WorkerPresetTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("worker presets: unknown values fall back to the default preset", ParseAsync);
        TestRegistry.Add("worker presets: researcher is read-only and never edits or runs commands", ResearcherAsync);
        TestRegistry.Add("worker presets: reviewer may run tests and diagnostics but not edit", ReviewerAsync);
        TestRegistry.Add("worker presets: the default preset keeps every tool", DefaultAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task ParseAsync()
    {
        Assert(WorkerPresets.Parse("researcher") == WorkerPreset.Researcher, "researcher parses");
        Assert(WorkerPresets.Parse("review") == WorkerPreset.Reviewer, "review maps to reviewer");
        Assert(WorkerPresets.Parse("QA") == WorkerPreset.Reviewer, "qa maps to reviewer");
        Assert(WorkerPresets.Parse("default") == WorkerPreset.Default, "default parses");
        Assert(WorkerPresets.Parse("something-else") == WorkerPreset.Default, "an unknown preset falls back to default");
        Assert(WorkerPresets.Parse(null) == WorkerPreset.Default, "null falls back to default");
        return Task.CompletedTask;
    }

    private static Task ResearcherAsync()
    {
        var tools = WorkerPresets.AllowedTools(WorkerPreset.Researcher);
        Assert(tools is not null, "researcher has an explicit tool set");
        foreach (var allowed in new[] { "file_read", "grep", "glob", "repo_map", "web_search", "fetch_web_page", "read_document_file" })
            Assert(tools!.Contains(allowed), $"researcher must keep {allowed}");
        foreach (var forbidden in new[] { "file_write", "str_replace_editor", "apply_patch", "bash", "github_action", "spawn_agents" })
            Assert(!tools!.Contains(forbidden), $"researcher must not get {forbidden}");
        return Task.CompletedTask;
    }

    private static Task ReviewerAsync()
    {
        var tools = WorkerPresets.AllowedTools(WorkerPreset.Reviewer);
        Assert(tools is not null, "reviewer has an explicit tool set");
        Assert(tools!.Contains("run_tests") && tools.Contains("get_diagnostics"), "reviewer can run tests and diagnostics");
        Assert(tools.Contains("file_read") && tools.Contains("grep"), "reviewer can read code");
        Assert(!tools.Contains("file_write") && !tools.Contains("apply_patch") && !tools.Contains("bash"),
            "reviewer must not change code or run arbitrary commands");
        return Task.CompletedTask;
    }

    private static Task DefaultAsync()
    {
        Assert(WorkerPresets.AllowedTools(WorkerPreset.Default) is null,
            "the default preset returns null, which AgentProfile treats as 'every tool'");
        Assert(WorkerPresets.Describe(WorkerPreset.Researcher).Contains("researcher", StringComparison.Ordinal),
            "the preset is described in the helper prompt");
        Assert(WorkerPresets.Describe(WorkerPreset.Reviewer).Contains("reviewer", StringComparison.Ordinal),
            "the reviewer mode is described too");
        return Task.CompletedTask;
    }
}
