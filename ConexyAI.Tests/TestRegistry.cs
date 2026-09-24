using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// TEST_SUITES: добавлено 2026-09-24
/// <summary>
/// Suites that live in their own files register here from a <c>[ModuleInitializer]</c>, so adding
/// a suite never touches the shared list in Program.cs. Program.cs runs everything registered after
/// its own inline tests.
/// </summary>
internal static class TestRegistry
{
    internal sealed record Entry(string Name, Func<Task> Body, Func<string?>? SkipReason);

    private static readonly List<Entry> _tests = new();

    internal static IReadOnlyList<Entry> Tests => _tests;

    /// <summary>Registers a test. <paramref name="skipReason"/> returns non-null to skip it.</summary>
    internal static void Add(string name, Func<Task> body, Func<string?>? skipReason = null) =>
        _tests.Add(new Entry(name, body, skipReason));

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Assertion failed: {message}");
    }
}
