namespace ConexyAI.Contract;

// DEBUG_TRACE: добавлено 2026-10-05 — «уровень A» отладчика: точки останова + трассировка
// выполнения Python в одну команду песочницы (sys.settrace). Это не интерактивный debug adapter
// (он требует постоянного канала в контейнер), а снимки выполнения: какие строки прошли, значения
// локальных переменных и стек вызовов в каждой записанной точке.

/// <summary>A breakpoint: a workspace-relative file and a 1-based line.</summary>
public sealed record DebugBreakpoint(string Path, int Line);

public sealed record DebugBreakpointsRequest(IReadOnlyList<DebugBreakpoint>? Breakpoints);

public sealed record DebugBreakpointsResult(bool Success, IReadOnlyList<DebugBreakpoint> Breakpoints, string? Error);

/// <summary>Body of a debug run: the entry script plus the breakpoints and the recording scope.</summary>
public sealed record DebugRunRequest(
    string? Path,
    IReadOnlyList<DebugBreakpoint>? Breakpoints,
    bool RecordAll);

/// <summary>One recorded execution point: source location, current frame locals and call stack.</summary>
public sealed record DebugStep(
    string File,
    int Line,
    string Function,
    IReadOnlyDictionary<string, string> Locals,
    IReadOnlyList<string> Stack,
    bool Breakpoint);

public sealed record DebugResult(
    bool Success,
    string? Language,
    IReadOnlyList<DebugStep> Steps,
    string Output,
    string? ProgramError,
    string? Error,
    bool Truncated,
    int ExitCode);
