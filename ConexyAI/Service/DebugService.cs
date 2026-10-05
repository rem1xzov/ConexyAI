using System.Text;
using System.Text.Json;
using ConexyAI.Contract;

namespace ConexyAI.Service;

// DEBUG_TRACE: добавлено 2026-10-05 — «уровень A» отладчика для IDE. Точки останова хранятся в
// рабочей области (.conexy/debug-breakpoints.json), а запуск генерирует harness и выполняет целевой
// Python-скрипт под sys.settrace в той же одноразовой песочнице, что и остальные команды. Harness
// пишет снимки выполнения (файл:строка, локальные переменные, стек вызовов) в JSON, который читает
// бэкенд. Настоящий пошаговый debug adapter здесь невозможен: у песочницы нет постоянного канала.
public interface IDebugService
{
    Task<DebugBreakpointsResult> GetBreakpointsAsync(Guid chatId, CancellationToken ct = default);

    Task<DebugBreakpointsResult> SetBreakpointsAsync(Guid chatId, IReadOnlyList<DebugBreakpoint> breakpoints, CancellationToken ct = default);

    Task<DebugResult> RunAsync(Guid chatId, DebugRunRequest request, CancellationToken ct = default);
}

public class ConexyDebugService : IDebugService
{
    private const string ConfigRelativePath = ".conexy/debug_config.json";
    private const string HarnessRelativePath = ".conexy/debug_harness.py";
    private const string TraceRelativePath = ".conexy/debug_trace.json";
    private const string BreakpointsRelativePath = ".conexy/debug-breakpoints.json";

    // Bounds so one run cannot return an unbounded trace or a huge JSON document.
    private const int MaxSteps = 2000;
    private const int MaxLocals = 40;
    private const int MaxValueLength = 200;
    private const int MaxBreakpoints = 200;
    private const int RunTimeoutSeconds = 120;

    // camelCase so the file matches the API/frontend shape (direct System.Text.Json does not apply the
    // web defaults that MVC uses).
    private static readonly JsonSerializerOptions BreakpointJson = new(JsonSerializerDefaults.Web);

    private readonly IConexyWorkspaceService _workspace;
    private readonly IConexyBashService _bash;
    private readonly ILogger<ConexyDebugService> _logger;

    public ConexyDebugService(IConexyWorkspaceService workspace, IConexyBashService bash, ILogger<ConexyDebugService> logger)
    {
        _workspace = workspace;
        _bash = bash;
        _logger = logger;
    }

    public async Task<DebugBreakpointsResult> GetBreakpointsAsync(Guid chatId, CancellationToken ct = default)
    {
        var root = _workspace.GetTaskWorkspacePathIfExists(chatId);
        if (root is null)
            return new DebugBreakpointsResult(true, Array.Empty<DebugBreakpoint>(), null);

        var file = Path.Combine(root, BreakpointsRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file))
            return new DebugBreakpointsResult(true, Array.Empty<DebugBreakpoint>(), null);

        try
        {
            var json = await File.ReadAllTextAsync(file, ct);
            using var doc = JsonDocument.Parse(json);
            var list = new List<DebugBreakpoint>();
            if (doc.RootElement.TryGetProperty("breakpoints", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var path = item.TryGetProperty("path", out var p) ? p.GetString() : null;
                    var line = item.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                    if (!string.IsNullOrWhiteSpace(path) && line > 0)
                        list.Add(new DebugBreakpoint(Normalize(path), line));
                }
            }

            return new DebugBreakpointsResult(true, list, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Could not read breakpoints for chat {ChatId}", chatId);
            return new DebugBreakpointsResult(true, Array.Empty<DebugBreakpoint>(), null);
        }
    }

    public async Task<DebugBreakpointsResult> SetBreakpointsAsync(Guid chatId, IReadOnlyList<DebugBreakpoint> breakpoints, CancellationToken ct = default)
    {
        var root = _workspace.GetTaskWorkspacePathIfExists(chatId);
        if (root is null)
        {
            // A draft chat has no workspace yet: keep them only in the editor until files exist.
            return new DebugBreakpointsResult(true, breakpoints ?? Array.Empty<DebugBreakpoint>(), null);
        }

        var clean = (breakpoints ?? Array.Empty<DebugBreakpoint>())
            .Where(b => !string.IsNullOrWhiteSpace(b.Path) && b.Line > 0 && !b.Path.Contains("..", StringComparison.Ordinal))
            .Select(b => new DebugBreakpoint(Normalize(b.Path), b.Line))
            .Take(MaxBreakpoints)
            .ToList();

        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".conexy"));
            var file = Path.Combine(root, BreakpointsRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var json = JsonSerializer.Serialize(new { breakpoints = clean }, BreakpointJson);
            await File.WriteAllTextAsync(file, json, Encoding.UTF8, ct);
            return new DebugBreakpointsResult(true, clean, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save breakpoints for chat {ChatId}", chatId);
            return new DebugBreakpointsResult(false, clean, "SAVE_FAILED");
        }
    }

    public async Task<DebugResult> RunAsync(Guid chatId, DebugRunRequest request, CancellationToken ct = default)
    {
        var root = _workspace.GetTaskWorkspacePathIfExists(chatId);
        if (root is null)
            return Failed("NO_WORKSPACE");

        var entry = Normalize(request.Path ?? string.Empty);
        if (entry.Length == 0)
            return Failed("NO_ENTRY");
        if (!entry.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            return Failed("UNSUPPORTED_LANGUAGE");

        string fullEntry;
        try
        {
            fullEntry = _workspace.ValidateWorkspacePath(Path.GetFullPath(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar))));
        }
        catch (UnauthorizedAccessException)
        {
            return Failed("NO_ENTRY");
        }

        if (!File.Exists(fullEntry))
            return Failed("ENTRY_NOT_FOUND");

        var breakpoints = (request.Breakpoints ?? Array.Empty<DebugBreakpoint>())
            .Where(b => !string.IsNullOrWhiteSpace(b.Path) && b.Line > 0 && !b.Path.Contains("..", StringComparison.Ordinal))
            .Select(b => new DebugBreakpoint(Normalize(b.Path), b.Line))
            .Take(MaxBreakpoints)
            .ToList();

        // With explicit breakpoints we record just those hits (a focused snapshot); otherwise the
        // whole execution inside the project is recorded (a bounded step replay).
        var recordAll = request.RecordAll || breakpoints.Count == 0;

        var directory = Path.Combine(root, ".conexy");
        var harnessFile = Path.Combine(root, HarnessRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var configFile = Path.Combine(root, ConfigRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var traceFile = Path.Combine(root, TraceRelativePath.Replace('/', Path.DirectorySeparatorChar));

        var config = new
        {
            target = "/workspace/" + entry,
            @out = "/workspace/" + TraceRelativePath,
            recordAll,
            maxSteps = MaxSteps,
            maxLocals = MaxLocals,
            maxValueLen = MaxValueLength,
            breakpoints = breakpoints.Select(b => new { path = b.Path, line = b.Line }).ToList(),
        };

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(harnessFile, HarnessScript, Encoding.UTF8, ct);
            await File.WriteAllTextAsync(configFile, JsonSerializer.Serialize(config), Encoding.UTF8, ct);
            if (File.Exists(traceFile)) File.Delete(traceFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not prepare the debug harness for chat {ChatId}", chatId);
            return Failed("PREPARE_FAILED");
        }

        BashToolResult run;
        try
        {
            run = await _bash.ExecuteAsync(chatId, new BashToolRequest
            {
                Command = $"python3 {HarnessRelativePath} {ConfigRelativePath}",
                TimeoutSeconds = RunTimeoutSeconds,
                RawOutput = true,
                IsDangerous = false,
            }, emitStartEvent: false, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Debug run failed to start for chat {ChatId}", chatId);
            return Failed("RUN_FAILED");
        }
        finally
        {
            TryDelete(configFile);
            TryDelete(harnessFile);
        }

        if (run.ErrorType is "workspace_not_found")
        {
            TryDelete(traceFile);
            return Failed("NO_WORKSPACE");
        }
        if (run.ErrorType is "spawn_failed")
        {
            TryDelete(traceFile);
            return new DebugResult(false, "python", Array.Empty<DebugStep>(), run.Output, null, "SANDBOX_UNAVAILABLE", false, run.ExitCode);
        }

        if (!File.Exists(traceFile))
        {
            var error = run.WasTimedOut ? "TIMEOUT" : "DEBUG_FAILED";
            return new DebugResult(false, "python", Array.Empty<DebugStep>(), run.Output, null, error, false, run.ExitCode);
        }

        string traceJson;
        try
        {
            traceJson = await File.ReadAllTextAsync(traceFile, ct);
        }
        catch (IOException)
        {
            return new DebugResult(false, "python", Array.Empty<DebugStep>(), run.Output, null, "DEBUG_FAILED", false, run.ExitCode);
        }
        finally
        {
            TryDelete(traceFile);
        }

        return ParseTrace(traceJson, run);
    }

    private DebugResult ParseTrace(string json, BashToolResult run)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var steps = new List<DebugStep>();
            if (doc.RootElement.TryGetProperty("steps", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var file = item.TryGetProperty("file", out var f) ? f.GetString() ?? string.Empty : string.Empty;
                    var line = item.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                    var function = item.TryGetProperty("function", out var fn) ? fn.GetString() ?? string.Empty : string.Empty;
                    var isBreakpoint = item.TryGetProperty("breakpoint", out var bp) && bp.ValueKind == JsonValueKind.True;

                    var locals = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (item.TryGetProperty("locals", out var localsEl) && localsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in localsEl.EnumerateObject())
                            locals[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? string.Empty : prop.Value.ToString();
                    }

                    var stack = new List<string>();
                    if (item.TryGetProperty("stack", out var stackEl) && stackEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var frame in stackEl.EnumerateArray())
                            stack.Add(frame.GetString() ?? string.Empty);
                    }

                    steps.Add(new DebugStep(file, line, function, locals, stack, isBreakpoint));
                }
            }

            var truncated = doc.RootElement.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True;
            string? programError = doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                ? err.GetString()
                : null;

            return new DebugResult(true, "python", steps, run.Output, programError, null, truncated, run.ExitCode);
        }
        catch (JsonException)
        {
            return new DebugResult(false, "python", Array.Empty<DebugStep>(), run.Output, null, "DEBUG_FAILED", false, run.ExitCode);
        }
    }

    private static DebugResult Failed(string error) =>
        new(false, null, Array.Empty<DebugStep>(), string.Empty, null, error, false, -1);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    // DEBUG_TRACE harness. Runs the target under sys.settrace and writes a JSON trace; it must never
    // contain a triple double-quote (C# raw string literal) and must not rely on the working directory.
    // Public so the test suite can execute it with the host Python and verify the trace format.
    public const string HarnessScript = """
        import json, os, runpy, sys, traceback

        _cfg_path = sys.argv[1]
        with open(_cfg_path, "r", encoding="utf-8") as _cfg_file:
            _cfg = json.load(_cfg_file)

        _root = os.path.realpath(os.getcwd())
        _target = _cfg["target"]
        _out_path = _cfg["out"]
        _record_all = bool(_cfg.get("recordAll", False))
        _max_steps = int(_cfg.get("maxSteps", 2000))
        _max_locals = int(_cfg.get("maxLocals", 40))
        _max_value = int(_cfg.get("maxValueLen", 200))
        _bps = set((str(b.get("path", "")), int(b.get("line", 0))) for b in _cfg.get("breakpoints", []))

        _steps = []
        _truncated = False
        _stopped = False
        _rel_cache = {}

        def _rel(path):
            if path in _rel_cache:
                return _rel_cache[path]
            result = None
            try:
                real = os.path.realpath(path)
                if real == _root or real.startswith(_root + os.sep):
                    result = os.path.relpath(real, _root).replace(os.sep, "/")
            except Exception:
                result = None
            _rel_cache[path] = result
            return result

        def _safe(value):
            try:
                text = repr(value)
            except Exception:
                try:
                    text = "<" + type(value).__name__ + ">"
                except Exception:
                    text = "<unrepr>"
            if len(text) > _max_value:
                text = text[:_max_value] + "..."
            return text

        def _tracer(frame, event, arg):
            global _truncated, _stopped
            if event == "call":
                return None if _stopped else _tracer
            if event != "line" or _stopped:
                return _tracer
            filename = frame.f_code.co_filename
            if filename.endswith("debug_harness.py"):
                return _tracer
            rel = _rel(filename)
            if rel is None or not rel.endswith(".py"):
                return _tracer
            line = frame.f_lineno
            is_bp = (rel, line) in _bps
            if not _record_all and not is_bp:
                return _tracer
            if len(_steps) >= _max_steps:
                _truncated = True
                _stopped = True
                return None
            local = {}
            try:
                items = list(frame.f_locals.items())
            except Exception:
                items = []
            shown = 0
            for name, value in items:
                if name.startswith("__"):
                    continue
                if shown >= _max_locals:
                    local["..."] = "..."
                    break
                local[name] = _safe(value)
                shown += 1
            stack = []
            walker = frame
            while walker is not None and len(stack) < 50:
                if walker.f_code.co_filename.endswith(".py") and _rel(walker.f_code.co_filename) is not None:
                    stack.append(walker.f_code.co_name)
                walker = walker.f_back
            _steps.append({
                "file": rel,
                "line": line,
                "function": frame.f_code.co_name,
                "locals": local,
                "stack": stack,
                "breakpoint": is_bp,
            })
            return _tracer

        _error = None
        sys.settrace(_tracer)
        try:
            runpy.run_path(_target, run_name="__main__")
        except SystemExit:
            pass
        except BaseException:
            _error = traceback.format_exc()
        finally:
            sys.settrace(None)

        with open(_out_path, "w", encoding="utf-8") as _out_file:
            json.dump({"steps": _steps, "truncated": _truncated, "error": _error}, _out_file)
        """;
}
