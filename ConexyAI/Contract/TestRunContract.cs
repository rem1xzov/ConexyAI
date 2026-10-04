namespace ConexyAI.Contract;

// TEST_RUNNER: добавлено 2026-10-04 — прогон тестов отдаёт структурированный отчёт (кто упал, файл:строка,
// ожидали/получили), а не тонну сырых логов, которые модель читает глазами.
public enum TestFramework
{
    Unknown,
    Pytest,
    Jest,
    Vitest,
    Mocha,
    DotnetTest,
    GoTest,
    CargoTest,
    RSpec,
    PhpUnit
}

/// <summary>Result of auto-detecting how to run the tests in a workspace (or a harmless error).</summary>
public sealed record TestCommandDetection(string? Command, TestFramework Framework, string? Error);

/// <summary>A single failing test with the details the model needs to fix it.</summary>
public sealed record TestFailure(
    string Name,
    string? File,
    int? Line,
    string? Expected,
    string? Actual,
    string? Message);

/// <summary>Structured summary of one test run.</summary>
public sealed record TestRunSummary(
    TestFramework Framework,
    bool Parsed,
    int Passed,
    int Failed,
    int Skipped,
    int Total,
    string? Duration,
    IReadOnlyList<TestFailure> Failures,
    string OutputTail,
    bool OutputTruncated);
