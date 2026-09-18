using System.Collections.Concurrent;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Hub;
using ConexyAI.Repository;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
/// <summary>
/// Coordinates the dangerous-command confirmation flow across scopes. The agent loop
/// (scoped background worker) registers a pending action and awaits a
/// <see cref="TaskCompletionSource{TResult}"/>; a later <c>ConfirmAction</c> hub call
/// (separate SignalR scope) resolves it, waking the loop to either run or reject the
/// command. Registered as a singleton so the waiter and the resolver share state.
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
        Guid chatId,
        Guid taskId,
        string command,
        string workingDirectory,
        CancellationToken ct);

    /// <summary>Resolves the waiter for <paramref name="actionId"/> with the user's decision.</summary>
    Task<bool> ConfirmActionAsync(Guid actionId, bool approved, CancellationToken ct);
}

public class PendingActionService : IPendingActionService
{
    // Safety net: a client that never answers must not deadlock the agent loop forever.
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ConexyHub> _hubContext;
    private readonly ILogger<PendingActionService> _logger;

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _waiters = new();

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
        Guid chatId,
        Guid taskId,
        string command,
        string workingDirectory,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPendingActionRepository>();

        await repository.CreateAsync(new PendingActionEntity
        {
            Id = actionId,
            ChatId = chatId,
            TaskId = taskId,
            Command = command,
            WorkingDirectory = workingDirectory,
            Status = PendingActionStatus.Pending
        }, ct);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_waiters.TryAdd(actionId, tcs))
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
            CreatedAt = DateTime.UtcNow
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

    public async Task<bool> ConfirmActionAsync(Guid actionId, bool approved, CancellationToken ct)
    {
        if (_waiters.TryRemove(actionId, out var tcs))
        {
            var status = approved ? PendingActionStatus.Approved : PendingActionStatus.Rejected;
            await MarkResolvedAsync(actionId, status, ct);
            tcs.TrySetResult(approved);
            _logger.LogInformation("Dangerous command {ActionId} {Decision}.", actionId, approved ? "approved" : "rejected");
            return true;
        }

        _logger.LogWarning("ConfirmAction for unknown/expired action {ActionId}.", actionId);
        return false;
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
