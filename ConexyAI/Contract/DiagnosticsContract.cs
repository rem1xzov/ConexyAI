namespace ConexyAI.Contract;

// DIAGNOSTICS: добавлено 2026-10-04 — структурированные ошибки компиляции/типов/линтера без запуска
// приложения и тестов («мозги редактора» настолько, насколько это безопасно делать в песочнице).
public enum DiagnosticTool
{
    Unknown,
    DotnetBuild,
    TypeScript,
    Eslint,
    Cargo,
    GoVet,
    Pyright,
    Mypy,
    Ruff
}

/// <summary>One compiler/linter diagnostic with a precise location.</summary>
public sealed record Diagnostic(
    string File,
    int Line,
    int Column,
    string Severity,
    string? Code,
    string Message);

/// <summary>Result of auto-detecting how to check a workspace (or a harmless error).</summary>
public sealed record DiagnosticCommandDetection(string? Command, DiagnosticTool Tool, string? Error);

/// <summary>Structured summary of one diagnostics run.</summary>
public sealed record DiagnosticReport(
    DiagnosticTool Tool,
    bool Parsed,
    int Errors,
    int Warnings,
    IReadOnlyList<Diagnostic> Diagnostics,
    string OutputTail,
    bool OutputTruncated);
