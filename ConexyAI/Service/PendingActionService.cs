using System.Collections.Concurrent;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Hub;
using ConexyAI.Repository;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

// COMMAND_CONFIRM: расширено 2026-09-20 — подтверждение теперь требуется для любой bash-команды,
// поэтому сервис больше не описывает только "опасные" команды.

// CONFIRM_GATE: добавлено 2026-09-22
/// <summary>
/// Thrown when the confirmation gate itself could not be established (the pending action could
/// not be persisted or announced). The command must NOT run in that case: without a working gate
/// there is nothing to approve, and silently executing it is exactly the bug where the card was
/// rendered but the agent kept going.
/// </summary>
public sealed class CommandConfirmationException : Exception
{
    public CommandConfirmationException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Coordinates the command-confirmation flow across scopes. The agent loop (scoped background
/// worker) registers a pending action and awaits a <see cref="TaskCompletionSource{TResult}"/>;
/// a later <c>ConfirmAction</c> hub call (separate SignalR scope) resolves it, waking the loop
/// to either run or reject the command. Registered as a singleton so the waiter and the
/// resolver share state.
/// </summary>
public interface IPendingActionService
{
    /// <summary>
    /// Persists a pending action, broadcasts <c>OnPendingActionCreated</c> to the task
    /// group, and blocks until the user confirms. Returns <c>true</c> (approved) or
    /// <c>false</c> (rejected); throws <see cref="CommandConfirmationException"/> when the gate
    /// itself could not be established (the caller must then NOT execute the command), and
    /// <see cref="OperationCanceledException"/> on cancellation/timeout.
    /// </summary>
    Task<bool> WaitForDecisionAsync(
        Guid actionId,
        Guid userId,
        Guid chatId,
        Guid taskId,
        string command,
        string workingDirectory,
        bool isDangerous,
        CancellationToken ct);

    /// <summary>
    /// Resolves the waiter for <paramref name="actionId"/> with the user's decision. When
    /// <paramref name="approveAll"/> is set on an approval, every later command of the same
    /// task runs without asking again ("allow all for this session").
    /// </summary>
    Task<bool> ConfirmActionAsync(Guid actionId, bool approved, bool approveAll, CancellationToken ct);

    /// <summary>
    /// True when the user switched the task to "allow all" — the agent then skips the
    /// confirmation round-trip and runs its commands immediately.
    /// </summary>
    bool IsTaskAutoApproved(Guid taskId);

    /// <summary>
    /// Turns "allow all" on/off for a task. Cleared when the task finishes so the next
    /// agent run always starts by asking again.
    /// </summary>
    void SetTaskAutoApproval(Guid taskId, bool enabled);
}

public class PendingActionService : IPendingActionService
{
    // Safety net: a client that never answers must not deadlock the agent loop forever.
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ConexyHub> _hubContext;
    private readonly ILogger<PendingActionService> _logger;

    private readonly ConcurrentDictionary<Guid, Waiter> _waiters = new();
    // Tasks the user chose to auto-approve ("allow all for this session").
    private readonly ConcurrentDictionary<Guid, byte> _autoApprovedTasks = new();

    /// <summary>A blocking waiter plus the task it belongs to, so "allow all" can be scoped.</summary>
    private sealed record Waiter(TaskCompletionSource<bool> Tcs, Guid TaskId);

    public PendingActionService(
        IServiceScopeFactory scopeFactory,
        IHubContext<ConexyHub> hubContext,
        ILogger<PendingActionService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<bool> WaitForDecisionAsync(
        Guid actionId,
        Guid userId,
        Guid chatId,
        Guid taskId,
        string command,
        string workingDirectory,
        bool isDangerous,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPendingActionRepository>();

        // CONFIRM_GATE: добавлено 2026-09-22 — если состояние подтверждения не удаётся сохранить,
        // бросаем CommandConfirmationException, а не пропускаем команду без подтверждения.
        // Раньше это исключение съедалось в DispatchToolAsync, агент продолжал работу, а карточка
        // навсегда оставалась «ожидающей» — ровно тот баг, когда кнопки ничего не решают.
        try
        {
            await repository.CreateAsync(new PendingActionEntity
            {
                Id = actionId,
                ChatId = chatId,
                TaskId = taskId,
                UserId = userId,
                Command = command,
                WorkingDirectory = workingDirectory,
                Status = PendingActionStatus.Pending
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist pending action {ActionId} for task {TaskId}.", actionId, taskId);
            throw new CommandConfirmationException(
                $"Не удалось создать запрос подтверждения команды: {ex.Message}", ex);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_waiters.TryAdd(actionId, new Waiter(tcs, taskId)))
        {
            // Duplicate action id: treat as approved so the loop can proceed rather than hang.
            return true;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConfirmationTimeout);
        var waitToken = timeoutCts.Token;

        try
        {
            await _hubContext.Clients.Group($"task_{taskId}").SendAsync("OnPendingActionCreated", new PendingActionEvent
            {
                ActionId = actionId,
                ChatId = chatId,
                TaskId = taskId,
                Command = command,
                WorkingDirectory = workingDirectory,
                Status = "Pending",
                CreatedAt = DateTime.UtcNow,
                IsDangerous = isDangerous
            }, waitToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The card never reached the user, so there is nothing they could approve.
            _waiters.TryRemove(actionId, out _);
            await MarkResolvedQuietlyAsync(actionId, PendingActionStatus.Cancelled);
            _logger.LogError(ex, "Failed to broadcast pending action {ActionId}; command will not run.", actionId);
            throw new CommandConfirmationException(
                $"Не удалось показать запрос подтверждения команды: {ex.Message}", ex);
        }

        using var registration = waitToken.Register(() => tcs.TrySetCanceled(waitToken));

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await MarkResolvedQuietlyAsync(actionId, PendingActionStatus.Cancelled);
            throw;
        }
        finally
        {
            _waiters.TryRemove(actionId, out _);
        }
    }

    // CONFIRM_GATE: добавлено 2026-09-22 — лучшая попытка отметить действие разрешённым;
    // сбой записи не должен маскировать исходную ошибку или отмену.
    private async Task MarkResolvedQuietlyAsync(Guid actionId, PendingActionStatus status)
    {
        try
        {
            await MarkResolvedAsync(actionId, status, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark pending action {ActionId} as {Status}.", actionId, status);
        }
    }

    public async Task<bool> ConfirmActionAsync(Guid actionId, bool approved, bool approveAll, CancellationToken ct)
    {
        if (_waiters.TryRemove(actionId, out var waiter))
        {
            if (approved && approveAll)
            {
                _autoApprovedTasks[waiter.TaskId] = 1;
            }

            var status = approved ? PendingActionStatus.Approved : PendingActionStatus.Rejected;
            await MarkResolvedAsync(actionId, status, ct);
            waiter.Tcs.TrySetResult(approved);
            _logger.LogInformation(
                "Command {ActionId} {Decision} (allowAll={AllowAll}).",
                actionId,
                approved ? "approved" : "rejected",
                approveAll);
            return true;
        }

        _logger.LogWarning("ConfirmAction for unknown/expired action {ActionId}.", actionId);
        return false;
    }

    public bool IsTaskAutoApproved(Guid taskId) => _autoApprovedTasks.ContainsKey(taskId);

    public void SetTaskAutoApproval(Guid taskId, bool enabled)
    {
        if (enabled)
        {
            _autoApprovedTasks[taskId] = 1;
            return;
        }

        _autoApprovedTasks.TryRemove(taskId, out _);
    }

    private async Task MarkResolvedAsync(Guid actionId, PendingActionStatus status, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPendingActionRepository>();

        var entity = await repository.GetByIdAsync(actionId, ct);
        if (entity is null)
            return;

        entity.Status = status;
        entity.ResolvedAt = DateTime.UtcNow;
        await repository.UpdateAsync(entity, ct);
    }
}
