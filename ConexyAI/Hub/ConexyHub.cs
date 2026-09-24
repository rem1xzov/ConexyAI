using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Repository;
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
    // CHAT_OWNERSHIP: добавлено 2026-09-24 — ревью C1: каждый метод хаба проверяет владельца.
    private readonly IChatAccessService _chatAccess;
    private readonly IConexyQueueGuard _queueGuard;
    private readonly ISupportRepository _support;
    private readonly ISandboxTerminalService _sandboxTerminal;
    private readonly ILogger<ConexyHub> _logger;

    public ConexyHub(
        IIdeTerminalService terminalService,
        IConexyRunService runService,
        IConexyCancellationRegistry cancellations,
        IPendingActionService pendingActions,
        IChatAccessService chatAccess,
        IConexyQueueGuard queueGuard,
        ISupportRepository support,
        ISandboxTerminalService sandboxTerminal,
        ILogger<ConexyHub> logger)
    {
        _terminalService = terminalService;
        _runService = runService;
        _cancellations = cancellations;
        _pendingActions = pendingActions;
        _chatAccess = chatAccess;
        _queueGuard = queueGuard;
        _support = support;
        _sandboxTerminal = sandboxTerminal;
        _logger = logger;
    }

    // SIGNALR_RESILIENCE: добавлено 2026-09-22 — без этих строк невозможно понять, ПОЧЕМУ
    // оборвалось соединение: раньше хабу было нечего сказать про connect/disconnect, и причину
    // (idle-таймаут прокси, рестарт бэкенда, сеть клиента) приходилось угадывать.
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation(
            "SignalR connected: {ConnectionId} user={UserId} path={Path}",
            Context.ConnectionId, Context.UserIdentifier, Context.GetHttpContext()?.Request.Path.Value);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is null)
        {
            _logger.LogInformation("SignalR disconnected: {ConnectionId} (graceful).", Context.ConnectionId);
        }
        else
        {
            // The exception (or its absence) is the only clue to what killed a long-running stream.
            _logger.LogWarning(
                exception,
                "SignalR disconnected: {ConnectionId} with error: {Message}",
                Context.ConnectionId, exception.Message);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Joins the live stream of a turn (<c>task_{taskId}</c>) or of a chat workspace (<c>task_{chatId}</c>).
    /// CHAT_OWNERSHIP: ревью C1 — чужой поток (вывод команд, содержимое файлов, карточки подтверждения)
    /// больше не читается по одному известному id.
    /// </summary>
    public async Task JoinTask(string taskId)
    {
        if (!Guid.TryParse(taskId, out var id))
            throw new HubException("forbidden: invalid id");

        var userId = CallerId();
        if (!await _chatAccess.CanJoinScopeAsync(userId, id, Context.ConnectionAborted))
        {
            _logger.LogWarning("JoinTask refused: connection {ConnectionId} (user {UserId}) asked for {Id}.", Context.ConnectionId, userId, id);
            throw new HubException("forbidden");
        }

        _logger.LogInformation("JoinTask: connection {ConnectionId} joining task_{TaskId}", Context.ConnectionId, id);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"task_{id}");
    }

    public async Task LeaveTask(string taskId)
    {
        if (!Guid.TryParse(taskId, out var id))
            return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"task_{id}");
    }

    // SUPPORT: добавлено 2026-09-19
    /// <summary>Joins the real-time group for a support ticket (the ticket's owner or an admin).</summary>
    public async Task JoinSupportTicket(Guid ticketId)
    {
        // CHAT_OWNERSHIP: ревью C1 — раньше любой пользователь мог слушать чужой тикет поддержки.
        var ticket = await _support.GetTicketAsync(ticketId, Context.ConnectionAborted);
        if (ticket is null || (ticket.UserId != CallerId() && Context.User?.IsAdmin() != true))
            throw new HubException("forbidden");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"support_{ticketId}");
    }

    /// <summary>Leaves a support ticket's real-time group.</summary>
    public async Task LeaveSupportTicket(Guid ticketId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"support_{ticketId}");
    }

    /// <summary>
    /// Cancels the in-flight generation for the given task id. The background worker's
    /// per-task <see cref="CancellationToken"/> is cancelled, which aborts the upstream
    /// HTTP request (and any running agent tool) and emits an <c>OnStopped</c> event.
    /// STOP_CONFIRM: изменено 2026-09-24 — ревью M17: возвращает, что произошло на самом деле
    /// ("stopping" | "cancelled" | "not_found"), и отменяет задачу, которая ещё стоит в очереди.
    /// </summary>
    public async Task<string> StopGeneration(Guid taskId)
    {
        if (!await _chatAccess.IsTaskOwnerAsync(CallerId(), taskId, Context.ConnectionAborted))
            return "not_found";

        return _cancellations.Stop(taskId, _queueGuard.IsInFlight(taskId)) switch
        {
            StopOutcome.Stopping => "stopping",
            StopOutcome.Cancelled => "cancelled",
            _ => "not_found",
        };
    }

    // COMMAND_CONFIRM: расширено 2026-09-20 — подтверждение требуется для любой bash-команды.
    /// <summary>
    /// Resolves a paused <c>bash</c> command. Pass <paramref name="approveAll"/> with an approval
    /// to let every later command of the same task run without asking again.
    /// CHAT_OWNERSHIP: ревью C1 — решение принимает только владелец задачи.
    /// </summary>
    public async Task<bool> ConfirmAction(Guid actionId, bool approved, bool approveAll)
    {
        _logger.LogInformation(
            "ConfirmAction {ActionId} approved={Approved} approveAll={ApproveAll} by connection {ConnectionId}",
            actionId, approved, approveAll, Context.ConnectionId);
        return await _pendingActions.ConfirmActionAsync(actionId, CallerId(), approved, approveAll, Context.ConnectionAborted);
    }

    /// <summary>Turns "allow all commands for this task" on or off while the task is running.</summary>
    public async Task SetTaskAutoApproval(Guid taskId, bool enabled)
    {
        if (!await _chatAccess.IsTaskOwnerAsync(CallerId(), taskId, Context.ConnectionAborted))
            throw new HubException("forbidden");

        _logger.LogInformation("SetTaskAutoApproval task {TaskId} enabled={Enabled}", taskId, enabled);
        _pendingActions.SetTaskAutoApproval(taskId, enabled);
    }

    // ---- Interactive terminal (pty) ----

    /// <summary>Creates (or reuses) the session's interactive shell.</summary>
    public async Task StartTerminal(Guid sessionId)
    {
        await EnsureChatOwnerAsync(sessionId);
        await _terminalService.StartAsync(sessionId);
    }

    /// <summary>Forwards keystrokes to the session shell's stdin.</summary>
    public async Task SendInput(Guid sessionId, string data)
    {
        await EnsureChatOwnerAsync(sessionId);
        await _terminalService.SendInputAsync(sessionId, data);
    }

    /// <summary>Updates the session terminal window size.</summary>
    public async Task ResizeTerminal(Guid sessionId, int cols, int rows)
    {
        await EnsureChatOwnerAsync(sessionId);
        await _terminalService.ResizeAsync(sessionId, cols, rows);
    }

    /// <summary>Explicitly closes the session shell.</summary>
    public async Task StopTerminal(Guid sessionId)
    {
        await EnsureChatOwnerAsync(sessionId);
        await _terminalService.StopAsync(sessionId);
    }

    /// <summary>Returns the backend host OS so the UI can adapt (e.g. Run behavior on Windows).</summary>
    public string GetPlatform() => OperatingSystem.IsWindows() ? "windows" : "linux";

    // ---- Sandbox terminal (line mode) ----
    // SANDBOX_TERMINAL: добавлено 2026-09-24 — ТЗ 2, §6.

    /// <summary>"pty" | "sandbox" | "unavailable".</summary>
    public Task<string> GetTerminalMode() => _sandboxTerminal.GetModeAsync(Context.ConnectionAborted);

    /// <summary>Runs one user command in the chat's sandbox; output streams as TerminalOutput.</summary>
    public async Task<SandboxCommandResult> RunSandboxCommand(Guid chatId, string command)
    {
        await EnsureChatWritableAsync(chatId);
        return await _sandboxTerminal.RunAsync(chatId, command, Context.ConnectionAborted);
    }

    /// <summary>Cancels the chat's running user command.</summary>
    public async Task<bool> CancelSandboxCommand(Guid chatId)
    {
        await EnsureChatOwnerAsync(chatId);
        return _sandboxTerminal.Cancel(chatId);
    }

    // ---- Run project (Ctrl+F5) ----

    /// <summary>Runs the chat's detected/persisted run command in the interactive pty.</summary>
    public async Task<RunProjectResult> RunProject(Guid chatId)
    {
        _logger.LogInformation("RunProject invoked for chat {ChatId}", chatId);
        await EnsureChatOwnerAsync(chatId);
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
        await EnsureChatOwnerAsync(chatId);
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

    private Guid CallerId() =>
        Context.User?.TryGetUserId(out var userId) == true
            ? userId
            : throw new HubException("forbidden: no user id");

    private async Task EnsureChatOwnerAsync(Guid chatId)
    {
        if (await _chatAccess.GetAccessAsync(CallerId(), chatId, Context.ConnectionAborted) != ChatAccessKind.Owner)
            throw new HubException("forbidden");
    }

    private async Task EnsureChatWritableAsync(Guid chatId)
    {
        try
        {
            await _chatAccess.EnsureWritableAsync(CallerId(), chatId, ct: Context.ConnectionAborted);
        }
        catch (ChatAccessDeniedException)
        {
            throw new HubException("forbidden");
        }
    }
}
