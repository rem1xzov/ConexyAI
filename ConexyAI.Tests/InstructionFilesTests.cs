using System.Runtime.CompilerServices;
using ConexyAI.Service;

// INSTRUCTION_DIGEST: добавлено 2026-11-20
/// <summary>
/// Обновление правил проекта нужно только когда изменился файл правил/хуков, а не любая правка
/// исходника: иначе каждый прогон тратит лишний device-вызов и токены на перечитывание.
/// </summary>
internal static class InstructionFilesTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("instruction files: code edits do not trigger a rules refresh", CodeAsync);
        TestRegistry.Add("instruction files: rule and hook files are recognized by name or tail", RulesAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task CodeAsync()
    {
        Assert(!InstructionFiles.Touched(["src/Program.cs", "frontend/src/App.tsx"]),
            "changes of source files must not reload project rules");
        Assert(!InstructionFiles.Touched([]), "no changes — no refresh");
        Assert(!InstructionFiles.Touched(null), "null changes — no refresh");
        Assert(InstructionFiles.Touched(["src/Program.cs", "CONEXY.md"]),
            "a rules file among changes triggers the refresh");
        return Task.CompletedTask;
    }

    private static Task RulesAsync()
    {
        foreach (var path in new[]
                 {
                     "CONEXY.md", "CLAUDE.md", "AGENTS.md", ".conexy/rules.md", ".conexy/hooks.json",
                     "sub/CLAUDE.md", "./AGENTS.md", @"C:\repo\CONEXY.md", "./.conexy/rules.md",
                 })
        {
            Assert(InstructionFiles.IsInstructionFile(path), $"must recognize '{path}' as an instruction file");
        }

        Assert(!InstructionFiles.IsInstructionFile("README.md"), "README.md is not a rules file");
        Assert(!InstructionFiles.IsInstructionFile("src/CONEXY.md.bak"), "a backup is not the rules file");
        Assert(!InstructionFiles.IsInstructionFile(null), "null path is not a rules file");
        return Task.CompletedTask;
    }
}
