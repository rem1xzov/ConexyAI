using System.Collections.Concurrent;
using System.Text;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Hub;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// SANDBOX_TERMINAL: добавлено 2026-09-24 — ТЗ 2, §6: терминал в воркспейсе агента, где пользователь
// сам вводит команды в ту же песочницу. Интерактивный pty живёт вне песочницы (на хосте бэкенда) и
// в проде закрыт, поэтому здесь — построчный режим: каждая строка выполняется в Docker-песочнице
// ровно так же, как bash-команда агента (тот же воркспейс, то же состояние сессии, те же лимиты).

/// <summary>Result of one user command, returned to the terminal.</summary>
public sealed record SandboxCommandResult(int ExitCode, bool TimedOut, string? Error);

public interface ISandboxTerminalService
{
    /// <summary>"pty" (dev backend shell), "sandbox" (line mode in Docker) or "unavailable".</summary>
    Task<string> GetModeAsync(CancellationToken ct = default);

    /// <summary>Runs one user command in the chat's sandbox; output is broadcast as TerminalOutput.</summary>
    Task<SandboxCommandResult> RunAsync(Guid chatId, string command, CancellationToken ct = default);

    /// <summary>Cancels the chat's running user command. False when none is running.</summary>
    bool Cancel(Guid chatId);
}

public class SandboxTerminalService : ISandboxTerminalService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(120);
    private const int MaxOutputChars = 64_000;
    private const int MaxCommandChars = 4_000;

    private readonly IDockerSandboxRunner _sandbox;
    private readonly ISandboxSessionStore _sessions;
    private readonly ISandboxActivity _activity;
    private readonly IConexyWorkspaceService _workspace;
    private readonly IHubContext<ConexyHub> _hub;
    private readonly bool _allowBackendShell;
    private readonly ILogger<SandboxTerminalService> _logger;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    private (DateTime CheckedAt, bool Available) _dockerProbe;

    public SandboxTerminalService(
        IDockerSandboxRunner sandbox,
        ISandboxSessionStore sessions,
        ISandboxActivity activity,
        IConexyWorkspaceService workspace,
        IHubContext<ConexyHub> hub,
        IOptions<SandboxOptions> options,
        ILogger<SandboxTerminalService> logger)
    {
        _sandbox = sandbox;
        _sessions = sessions;
        _activity = activity;
        _workspace = workspace;
        _hub = hub;
        _allowBackendShell = options.Value.AllowBackendShell;
        _logger = logger;
    }

    public async Task<string> GetModeAsync(CancellationToken ct = default)
    {
        if (_allowBackendShell)
            return "pty";

        var probe = _dockerProbe;
        if (DateTime.UtcNow - probe.CheckedAt > TimeSpan.FromSeconds(30))
        {
            probe = (DateTime.UtcNow, await _sandbox.IsDockerAvailableAsync(ct));
            _dockerProbe = probe;
        }

        return probe.Available ? "sandbox" : "unavailable";
    }

    public async Task<SandboxCommandResult> RunAsync(Guid chatId, string command, CancellationToken ct = default)
    {
        command = (command ?? string.Empty).Trim();
        if (command.Length == 0)
            return new SandboxCommandResult(0, false, null);
        if (command.Length > MaxCommandChars)
            return new SandboxCommandResult(-1, false, "too_long");

        using var slot = _activity.TryAcquire(chatId);
        if (slot is null)
            return new SandboxCommandResult(-1, false, "busy");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_running.TryAdd(chatId, cts))
            return new SandboxCommandResult(-1, false, "busy");

        try
        {
            var workspace = _workspace.ValidateWorkspacePath(_workspace.GetTaskWorkspacePath(chatId));
            var statePath = _sessions.GetOrCreateStatePath(chatId);

            // Logged without the command text: it is the user's own input.
            _logger.LogInformation("Sandbox terminal command for chat {ChatId} ({Chars} chars).", chatId, command.Length);
            var result = await _sandbox.RunAsync(command, workspace, CommandTimeout, cts.Token, statePath);

            await SendAsync(chatId, Format(result), CancellationToken.None);
            return new SandboxCommandResult(result.ExitCode, result.WasTimedOut, result.Success ? null : result.ErrorType);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            await SendAsync(chatId, "\x1b[1;33m^C\x1b[0m\r\n", CancellationToken.None);
            return new SandboxCommandResult(130, false, "cancelled");
        }
        finally
        {
            _running.TryRemove(chatId, out _);
        }
    }

    public bool Cancel(Guid chatId)
    {
        if (!_running.TryGetValue(chatId, out var cts))
            return false;

        cts.Cancel();
        return true;
    }

    private Task SendAsync(Guid chatId, string data, CancellationToken ct) =>
        _hub.Clients.Group($"task_{chatId}").SendAsync("TerminalOutput", new TerminalOutputEvent
        {
            SessionId = chatId,
            Data = data,
            Source = "user"
        }, ct);

    private static string Format(SandboxExecutionResult result)
    {
        var output = result.Output ?? string.Empty;
        if (output.Length > MaxOutputChars)
            output = output[..MaxOutputChars] + "\n[... output truncated ...]";

        var sb = new StringBuilder();
        if (output.Length > 0)
        {
            sb.Append(output.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Replace("\n", "\r\n")).Append("\r\n");
        }

        if (result.WasTimedOut)
            sb.Append("\x1b[1;33m[timed out]\x1b[0m\r\n");
        else if (!result.Success)
            sb.Append("\x1b[1;31m[exit ").Append(result.ExitCode).Append("]\x1b[0m\r\n");

        return sb.ToString();
    }
}
