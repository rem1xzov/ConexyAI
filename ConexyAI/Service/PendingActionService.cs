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
    /// <c>false</c> (rejected); throws on cancellation/timeout.
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

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_waiters.TryAdd(actionId, new Waiter(tcs, taskId)))
        {
            // Duplicate action id: treat as approved so the loop can proceed rather than hang.
            return true;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConfirmationTimeout);
        var waitToken = timeoutCts.Token;

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

        using var registration = waitToken.Register(() => tcs.TrySetCanceled(waitToken));

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await MarkResolvedAsync(actionId, PendingActionStatus.Cancelled, ct);
            throw;
        }
        finally
        {
            _waiters.TryRemove(actionId, out _);
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
