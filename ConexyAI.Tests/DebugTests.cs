using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// DEBUG_TRACE: добавлено 2026-10-05 — тесты точек останова и trace-harness (уровень A отладчика).
internal static class DebugTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("debug: breakpoints round-trip through the chat workspace", BreakpointsRoundTripAsync);
        TestRegistry.Add("debug: entry validation refuses non-Python, empty and missing files before the sandbox", ValidationAsync);
        TestRegistry.Add("debug: the trace harness records lines, locals and breakpoints", HarnessTraceAsync, PythonSkipReason);
    }

    private static async Task BreakpointsRoundTripAsync()
    {
        var root = TempRoot();
        try
        {
            var workspace = WorkspaceService(root);
            var chatId = Guid.NewGuid();
            workspace.GetTaskWorkspacePath(chatId); // materialize the chat workspace

            var service = new ConexyDebugService(workspace, new NoopBash(), NullLogger<ConexyDebugService>.Instance);
            var saved = await service.SetBreakpointsAsync(chatId, new[]
            {
                new DebugBreakpoint("src/app.py", 12),
                new DebugBreakpoint("src\\util.py", 4),   // backslashes are normalized to '/'
                new DebugBreakpoint("../evil.py", 1),     // traversal rejected
                new DebugBreakpoint("x.py", 0),           // non-positive line rejected
            });

            TestRegistry.Assert(saved.Success, "breakpoints saved");
            TestRegistry.Assert(saved.Breakpoints.Count == 2, "two valid breakpoints kept, got " + saved.Breakpoints.Count);

            var loaded = await service.GetBreakpointsAsync(chatId);
            TestRegistry.Assert(loaded.Success && loaded.Breakpoints.Count == 2, "two breakpoints loaded");
            TestRegistry.Assert(loaded.Breakpoints.Any(b => b.Path == "src/app.py" && b.Line == 12), "src/app.py:12 kept");
            TestRegistry.Assert(loaded.Breakpoints.Any(b => b.Path == "src/util.py" && b.Line == 4), "backslashes normalized");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task ValidationAsync()
    {
        var root = TempRoot();
        try
        {
            var workspace = WorkspaceService(root);
            var chatId = Guid.NewGuid();
            workspace.GetTaskWorkspacePath(chatId);

            var service = new ConexyDebugService(workspace, new NoopBash(), NullLogger<ConexyDebugService>.Instance);

            var nonPython = await service.RunAsync(chatId, new DebugRunRequest("app.js", null, true));
            TestRegistry.Assert(!nonPython.Success && nonPython.Error == "UNSUPPORTED_LANGUAGE", "a .js entry is refused, got " + nonPython.Error);

            var empty = await service.RunAsync(chatId, new DebugRunRequest("", null, true));
            TestRegistry.Assert(!empty.Success && empty.Error == "NO_ENTRY", "an empty entry is refused, got " + empty.Error);

            var missing = await service.RunAsync(chatId, new DebugRunRequest("nope.py", null, true));
            TestRegistry.Assert(!missing.Success && missing.Error == "ENTRY_NOT_FOUND", "a missing file is refused, got " + missing.Error);

            var noWorkspace = await new ConexyDebugService(WorkspaceService(root), new NoopBash(), NullLogger<ConexyDebugService>.Instance)
                .RunAsync(Guid.NewGuid(), new DebugRunRequest("app.py", null, true));
            TestRegistry.Assert(!noWorkspace.Success && noWorkspace.Error == "NO_WORKSPACE", "a chat without a workspace is refused, got " + noWorkspace.Error);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task HarnessTraceAsync()
    {
        var python = FindPython()!.Value;
        var root = TempRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "app.py"),
                "def add(a, b):\n" +
                "    total = a + b\n" +
                "    return total\n" +
                "\n" +
                "if __name__ == \"__main__\":\n" +
                "    print(\"result\", add(2, 3))\n");

            var config = new
            {
                target = "app.py",
                @out = "trace.json",
                recordAll = true,
                maxSteps = 2000,
                maxLocals = 40,
                maxValueLen = 200,
                breakpoints = new[] { new { path = "app.py", line = 2 } },
            };
            await File.WriteAllTextAsync(Path.Combine(root, "debug_harness.py"), ConexyDebugService.HarnessScript);
            await File.WriteAllTextAsync(Path.Combine(root, "debug_config.json"), JsonSerializer.Serialize(config));

            var (exitCode, output) = RunProcess(python.File, (python.ArgsPrefix + " debug_harness.py debug_config.json").Trim(), root);
            var tracePath = Path.Combine(root, "trace.json");
            TestRegistry.Assert(File.Exists(tracePath), "the harness wrote trace.json (exit " + exitCode + "), output: " + output);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(tracePath));
            var steps = doc.RootElement.GetProperty("steps");
            TestRegistry.Assert(steps.GetArrayLength() > 0, "the trace has steps");

            var sawBreakpointA = false;
            var sawTotal = false;
            foreach (var step in steps.EnumerateArray())
            {
                if (step.GetProperty("file").GetString() != "app.py") continue;
                var line = step.GetProperty("line").GetInt32();
                var locals = step.GetProperty("locals");

                if (line == 2)
                {
                    TestRegistry.Assert(locals.TryGetProperty("a", out var a) && a.GetString() == "2", "a == 2 at line 2");
                    TestRegistry.Assert(locals.TryGetProperty("b", out var b) && b.GetString() == "3", "b == 3 at line 2");
                    sawBreakpointA = step.GetProperty("breakpoint").GetBoolean();
                }

                if (line == 3 && locals.TryGetProperty("total", out var total) && total.GetString() == "5")
                    sawTotal = true;
            }

            TestRegistry.Assert(sawBreakpointA, "line 2 is flagged as a breakpoint");
            TestRegistry.Assert(sawTotal, "total == 5 at the return line");
            TestRegistry.Assert(output.Contains("result 5"), "the program's stdout is preserved: " + output);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- helpers -----------------------------------------------------------------------------

    private static ConexyWorkspaceService WorkspaceService(string root) =>
        new(Options.Create(new WorkspaceOptions { RootPath = root }), NullLogger<ConexyWorkspaceService>.Instance);

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "conexy-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }

    private static (string File, string ArgsPrefix)? _python;
    private static bool _pythonProbed;

    private static (string File, string ArgsPrefix)? FindPython()
    {
        if (_pythonProbed) return _python;
        _pythonProbed = true;
        // "python3"/"python" on Linux/macOS, and the Windows launcher "py -3".
        foreach (var (file, prefix) in new[] { ("python3", ""), ("python", ""), ("py", "-3") })
        {
            try
            {
                var (code, output) = RunProcess(file, (prefix + " --version").Trim(), AppContext.BaseDirectory);
                if (code == 0 && output.Contains("Python 3", StringComparison.Ordinal))
                {
                    _python = (file, prefix);
                    break;
                }
            }
            catch
            {
                /* not installed; try the next candidate */
            }
        }
        return _python;
    }

    private static string? PythonSkipReason() => FindPython() is null ? "requires Python 3 on PATH" : null;

    private static (int ExitCode, string Output) RunProcess(string file, string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, output + "\n[timed out]");
        }
        return (process.ExitCode, output);
    }

    private sealed class NoopBash : IConexyBashService
    {
        public Task<BashToolResult> ExecuteAsync(Guid sessionId, BashToolRequest request, bool emitStartEvent = true, CancellationToken ct = default) =>
            Task.FromResult(new BashToolResult { Success = false, ExitCode = -1, Output = string.Empty, ErrorType = "spawn_failed" });
    }
}
