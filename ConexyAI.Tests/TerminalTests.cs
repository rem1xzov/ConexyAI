using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ConexyAI.Service;

// MULTI_TERMINAL: добавлено 2026-10-05 — тесты нормализации id терминала (ключ словаря pty-сессий).
internal static class TerminalTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("terminal ids: empty becomes main, unsafe chars are stripped and bounded", NormalizeAsync);
    }

    private static Task NormalizeAsync()
    {
        TestRegistry.Assert(TerminalIds.Normalize(null) == "main", "null -> main");
        TestRegistry.Assert(TerminalIds.Normalize("   ") == "main", "blank -> main");
        TestRegistry.Assert(TerminalIds.Normalize("term-1") == "term-1", "a safe id is kept");
        TestRegistry.Assert(TerminalIds.Normalize("a/b\\c;rm") == "abcrm", "unsafe characters are stripped");
        TestRegistry.Assert(TerminalIds.Normalize(new string('x', 80)).Length == 40, "the id is bounded to 40 chars");
        return Task.CompletedTask;
    }
}
