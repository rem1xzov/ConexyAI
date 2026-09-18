using ConexyAI.Contract;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Hub;

[Authorize]
public class ConexyHub : Microsoft.AspNetCore.SignalR.Hub
{
    private readonly IIdeTerminalService _terminalService;
    private readonly IConexyRunService _runService;
    private readonly IConexyCancellationRegistry _cancellations;
    private readonly IPendingActionService _pendingActions;
    private readonly ILogger<ConexyHub> _logger;

    public ConexyHub(
        IIdeTerminalService terminalService,
        IConexyRunService runService,
        IConexyCancellationRegistry cancellations,
        IPendingActionService pendingActions,
        ILogger<ConexyHub> logger)
    {
        _terminalService = terminalService;
        _runService = runService;
        _cancellations = cancellations;
        _pendingActions = pendingActions;
        _logger = logger;
    }

    public async Task JoinTask(string taskId)
    {
        _logger.LogInformation("JoinTask: connection {ConnectionId} joining task_{TaskId}", Context.ConnectionId, taskId);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"task_{taskId}");
    }

    public async Task LeaveTask(string taskId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"task_{taskId}");
    }

    /// <summary>
    /// Cancels the in-flight generation for the given task id. The background worker's
    /// per-task <see cref="CancellationToken"/> is cancelled, which aborts the upstream
    /// HTTP request (and any running agent tool) and emits an <c>OnStopped</c> event.
    /// </summary>
    public Task StopGeneration(Guid taskId)
    {
        _cancellations.Cancel(taskId);
        return Task.CompletedTask;
    }

    // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
    /// <summary>
    /// Resolves a paused dangerous <c>bash</c> command. Called by the frontend after the
    /// user approves or rejects the command shown in the confirmation modal.
    /// </summary>
    public async Task<bool> ConfirmAction(Guid actionId, bool approved)
    {
        _logger.LogInformation("ConfirmAction {ActionId} approved={Approved} by connection {ConnectionId}", actionId, approved, Context.ConnectionId);
        return await _pendingActions.ConfirmActionAsync(actionId, approved, Context.ConnectionAborted);
    }

    // ---- Interactive terminal (pty) ----

    /// <summary>Creates (or reuses) the session's interactive shell.</summary>
    public Task StartTerminal(Guid sessionId) => _terminalService.StartAsync(sessionId);

    /// <summary>Forwards keystrokes to the session shell's stdin.</summary>
    public Task SendInput(Guid sessionId, string data) => _terminalService.SendInputAsync(sessionId, data);

    /// <summary>Updates the session terminal window size.</summary>
    public Task ResizeTerminal(Guid sessionId, int cols, int rows) => _terminalService.ResizeAsync(sessionId, cols, rows);

    /// <summary>Explicitly closes the session shell.</summary>
    public Task StopTerminal(Guid sessionId) => _terminalService.StopAsync(sessionId);

    /// <summary>Returns the backend host OS so the UI can adapt (e.g. Run behavior on Windows).</summary>
    public string GetPlatform() => OperatingSystem.IsWindows() ? "windows" : "linux";

    // ---- Run project (Ctrl+F5) ----

    /// <summary>Runs the chat's detected/persisted run command in the interactive pty.</summary>
    public async Task<RunProjectResult> RunProject(Guid chatId)
    {
        _logger.LogInformation("RunProject invoked for chat {ChatId}", chatId);
        try
        {
            return await _runService.RunAsync(chatId, ct: Context.ConnectionAborted);
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            // Client disconnected mid-invoke — nothing to report, just stop cleanly.
            return new RunProjectResult { NeedsManualConfig = false };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunProject failed for chat {ChatId}", chatId);
            await Clients.Caller.SendAsync("RunProjectError", new { chatId, message = ex.Message });
            return new RunProjectResult { NeedsManualConfig = false, Error = ex.Message };
        }
    }

    /// <summary>Persists a manually-entered run command and runs it in the interactive pty.</summary>
    public async Task<RunProjectResult> RunProjectWithCommand(Guid chatId, string command)
    {
        _logger.LogInformation("RunProjectWithCommand invoked for chat {ChatId}", chatId);
        try
        {
            return await _runService.RunAsync(chatId, command, Context.ConnectionAborted);
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            return new RunProjectResult { NeedsManualConfig = false };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunProjectWithCommand failed for chat {ChatId}", chatId);
            await Clients.Caller.SendAsync("RunProjectError", new { chatId, message = ex.Message });
            return new RunProjectResult { NeedsManualConfig = false, Error = ex.Message };
        }
    }
}
