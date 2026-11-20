using System.Runtime.CompilerServices;
using ConexyAI.Service;

// DEVICE_PROJECT_FILES: добавлено 2026-11-20
/// <summary>
/// Один device-вызов читает и правила, и хуки; разбор ответа отделяет файлы правил от hooks.json,
/// чтобы JSON хуков не попал в блок правил проекта.
/// </summary>
internal static class DeviceProjectFilesTests
{
    [ModuleInitializer]
    internal static void Register()
        => TestRegistry.Add("device project files: rules and hooks are split without leaking hooks into rules", SplitAsync);

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task SplitAsync()
    {
        var files = new List<(string File, string Content)>
        {
            ("CONEXY.md", "rules text"),
            (".conexy/rules.md", "more rules"),
            (".conexy/hooks.json", "{\"hooks\":{}}"),
            ("unknown.txt", "ignored"),
        };
        string[] ruleFiles = ["CONEXY.md", "CLAUDE.md", "AGENTS.md", ".conexy/rules.md"];

        var (rules, hooks) = DeviceProjectFiles.Split(files, ".conexy/hooks.json", ruleFiles);
        Assert(rules.Count == 2, "only real rules files get into the rules list, got " + rules.Count);
        Assert(rules.All(file => file.File != ".conexy/hooks.json"), "hooks JSON must not leak into rules");
        Assert(hooks == "{\"hooks\":{}}", "the hooks content is extracted for the hooks service");

        var (noRules, noHooks) = DeviceProjectFiles.Split([], ".conexy/hooks.json", ruleFiles);
        Assert(noRules.Count == 0 && noHooks is null, "an empty client answer yields no rules and no hooks");
        return Task.CompletedTask;
    }
}
