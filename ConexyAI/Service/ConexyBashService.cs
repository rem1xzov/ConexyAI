using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using ConexyAI.Contract;
using ConexyAI.Hub;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

public interface IConexyBashService
{
    // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
    // emitStartEvent=false is used after a dangerous command is confirmed, so the feed
    // shows only pending_confirmation -> completed (no redundant "started" row).
    Task<BashToolResult> ExecuteAsync(Guid sessionId, BashToolRequest request, bool emitStartEvent = true, CancellationToken ct = default);
}

/// <summary>
/// Implements the <c>bash</c> tool: run a shell command inside the session workspace with a
/// hard timeout and output truncation. Registered as Scoped; a per-session semaphore
/// serializes concurrent invocations in the same workspace.
/// </summary>
public class ConexyBashService : IConexyBashService
{
    private readonly IConexyWorkspaceService _workspaceService;
    private readonly IDockerSandboxRunner _sandbox;
    private readonly IHubContext<ConexyHub> _hubContext;
    private readonly ILogger<ConexyBashService> _logger;
    // SANDBOX_SESSIONS: добавлено 2026-09-23 — состояние песочницы, живущее между командами.
    private readonly ISandboxSessionStore _sandboxSessions;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionLocks = new();

    private const int MaxTimeoutSeconds = 300;
    private const int DefaultTimeoutSeconds = 30;

    public ConexyBashService(
        IConexyWorkspaceService workspaceService,
        IDockerSandboxRunner sandbox,
        IHubContext<ConexyHub> hubContext,
        ILogger<ConexyBashService> logger,
        ISandboxSessionStore sandboxSessions)
    {
        _workspaceService = workspaceService;
        _sandbox = sandbox;
        _hubContext = hubContext;
        _logger = logger;
        _sandboxSessions = sandboxSessions;
    }

    public async Task<BashToolResult> ExecuteAsync(Guid sessionId, BashToolRequest request, bool emitStartEvent = true, CancellationToken ct = default)
    {
        var command = request.Command ?? string.Empty;

        if (emitStartEvent)
        {
            await SendToolActionAsync(sessionId, request, "started", $"Выполняю: {TruncateSummary(command)}", ct: ct);
        }

        BashToolResult result;
        try
        {
            result = await ExecuteCoreAsync(sessionId, request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "bash tool failed for session {SessionId}", sessionId);
            result = new BashToolResult { Success = false, ExitCode = -1, Output = "", ErrorType = "spawn_failed" };
        }

        var summary = result.WasTimedOut
            ? "timeout"
            : result.Success
                ? $"Exit code: {result.ExitCode}"
                : result.ErrorType;

        // COMMAND_CONFIRM: добавлено 2026-09-20
        // Include the (truncated) output so command cards can show it collapsed.
        await SendToolActionAsync(sessionId, request, result.Success ? "completed" : "failed", summary, result.Output, ct);

        // Mirror the command and its output into the interactive terminal feed so the
        // user sees agent work in the same chronological timeline as their own commands.
        await SendTerminalOutputAsync(sessionId, command, result, ct);

        // Surface structured build diagnostics for the IDE Problems panel.
        if (IsBuildCommand(command))
        {
            var problems = ParseBuildProblems(result.Output);
            await _hubContext.Clients.Group($"task_{sessionId}").SendAsync("BuildProblems", new BuildProblemsEvent { Problems = problems }, ct);
        }

        return result;
    }

    private async Task<BashToolResult> ExecuteCoreAsync(Guid sessionId, BashToolRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
            return new BashToolResult { Success = false, ExitCode = -1, Output = "", ErrorType = "spawn_failed" };

        var workspacePath = _workspaceService.GetTaskWorkspacePathIfExists(sessionId);
        if (workspacePath is null)
            return new BashToolResult { Success = false, ExitCode = -1, Output = "", ErrorType = "workspace_not_found" };

        // SANDBOX: добавлено 2026-09-17 — Path Jail before mapping the directory into Docker.
        workspacePath = _workspaceService.ValidateWorkspacePath(workspacePath);

        _logger.LogInformation("Executing bash for session {SessionId}: {Command}", sessionId, request.Command);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds));

        var semaphore = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        try
        {
            return await RunProcessAsync(sessionId, request.Command, workspacePath, timeout, ct);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<BashToolResult> RunProcessAsync(Guid sessionId, string command, string workingDir, TimeSpan timeout, CancellationToken ct)
    {
        // SANDBOX: добавлено 2026-09-17 — execute inside the isolated Docker container.
        // SANDBOX_SESSIONS: добавлено 2026-09-23 — контейнер по-прежнему одноразовый (создаётся под
        // команду и удаляется сразу после неё), но состояние сессии монтируется в /state, поэтому
        // установленные пакеты и кэши переживают переход к следующей команде, а сам каталог удаляется
        // sweeper'ом после 10 минут простоя.
        var statePath = _sandboxSessions.GetOrCreateStatePath(sessionId);
        var result = await _sandbox.RunAsync(command, workingDir, timeout, ct, statePath);
        var (text, truncated) = TruncateOutput(result.Output);

        return new BashToolResult
        {
            Success = result.Success,
            ExitCode = result.ExitCode,
            Output = text,
            WasTruncated = truncated,
            WasTimedOut = result.WasTimedOut,
            ErrorType = result.ErrorType
        };
    }

    private static (string text, bool truncated) TruncateOutput(string raw)
    {
        var lines = raw.Split('\n');
        const int headLines = 40;
        const int tailLines = 40;

        if (lines.Length <= headLines + tailLines)
            return (raw, false);

        var head = lines.Take(headLines);
        var tail = lines.Skip(lines.Length - tailLines);
        var omitted = lines.Length - headLines - tailLines;

        var result = string.Join('\n', head)
            + $"\n[... truncated {omitted} lines ...]\n"
            + string.Join('\n', tail);

        return (result, true);
    }

    private static string TruncateSummary(string command) =>
        command.Length <= 100 ? command : command[..100] + "...";

    private async Task SendTerminalOutputAsync(Guid sessionId, string command, BashToolResult result, CancellationToken ct)
    {
        var evt = new TerminalOutputEvent
        {
            SessionId = sessionId,
            Data = FormatAgentTerminalOutput(command, result),
            Source = "agent"
        };
        await _hubContext.Clients.Group($"task_{sessionId}").SendAsync("TerminalOutput", evt, ct);
    }

    private static string FormatAgentTerminalOutput(string command, BashToolResult result)
    {
        var sb = new StringBuilder();
        sb.Append("\x1b[1;35m[agent]\x1b[0m $ ").Append(NormalizeCrlf(command.TrimEnd('\n'))).Append("\r\n");

        if (!string.IsNullOrEmpty(result.Output))
            sb.Append(NormalizeCrlf(result.Output.TrimEnd('\n'))).Append("\r\n");

        if (result.WasTimedOut)
            sb.Append("\x1b[1;33m[agent] timed out\x1b[0m\r\n");
        else if (!result.Success)
            sb.Append("\x1b[1;31m[agent] failed: ").Append(result.ErrorType ?? $"exit code {result.ExitCode}").Append("\x1b[0m\r\n");

        return sb.ToString();
    }

    private static string NormalizeCrlf(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    // COMMAND_CONFIRM: добавлено 2026-09-20 — события несут IsDangerous/PendingActionId из запроса,
    // чтобы лента сохраняла акцент и связь с карточкой подтверждения.
    private async Task SendToolActionAsync(Guid sessionId, BashToolRequest request, string status, string? summary, string? output = null, CancellationToken ct = default)
    {
        var workspacePath = _workspaceService.GetTaskWorkspacePathIfExists(sessionId);
        var evt = new ToolActionEvent
        {
            ToolName = "bash",
            Command = request.Command ?? string.Empty,
            Path = "",
            Status = status,
            Summary = summary,
            WorkingDirectory = workspacePath,
            Output = output,
            PendingActionId = request.PendingActionId,
            IsDangerous = request.IsDangerous
        };
        await _hubContext.Clients.Group($"task_{sessionId}").SendAsync("ToolAction", evt, ct);
    }

    private static bool IsBuildCommand(string command)
    {
        var c = command.ToLowerInvariant();
        return c.Contains("dotnet build")
            || c.Contains("dotnet test")
            || c.Contains("dotnet publish")
            || c.Contains("msbuild")
            || c.Contains("npm run build")
            || c.Contains("tsc")
            || c.Contains("csc ")
            || c.Contains("cargo build")
            || c.Contains("go build");
    }

    private static readonly Regex BuildDiagnosticRegex = new(
        @"^(?<file>.+?)\((?<line>\d+)(?:,(?<col>\d+))?\):\s*(?<severity>error|warning)\s+(?<code>\S+):\s*(?<message>.*)$",
        RegexOptions.Compiled);

    private static List<BuildProblem> ParseBuildProblems(string output)
    {
        var problems = new List<BuildProblem>();
        if (string.IsNullOrWhiteSpace(output)) return problems;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = BuildDiagnosticRegex.Match(line);
            if (!match.Success) continue;

            problems.Add(new BuildProblem
            {
                File = match.Groups["file"].Value.Trim(),
                Line = int.TryParse(match.Groups["line"].Value, out var l) ? l : 0,
                Column = int.TryParse(match.Groups["col"].Value, out var col) ? col : 0,
                Severity = match.Groups["severity"].Value,
                Code = match.Groups["code"].Value,
                Message = match.Groups["message"].Value.Trim()
            });
        }

        return problems;
    }
}
