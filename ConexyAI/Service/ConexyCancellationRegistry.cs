using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

// STOP_CONFIRM: добавлено 2026-09-24 — ревью M17.
/// <summary>What a stop request actually did (returned to the client by the hub).</summary>
public enum StopOutcome
{
    /// <summary>A running turn is being cancelled; <c>OnStopped</c> follows.</summary>
    Stopping,

    /// <summary>The turn was still queued: it will be skipped when dequeued; <c>OnStopped</c> follows.</summary>
    Cancelled,

    /// <summary>Nothing in flight under that id.</summary>
    NotFound,
}

/// <summary>
/// Tracks a per-task <see cref="CancellationTokenSource"/> so an in-flight generation can
/// be cancelled on demand (the user's "Stop" button) without stopping the background worker
/// that owns the queue.
/// </summary>
public interface IConexyCancellationRegistry
{
    /// <summary>
    /// Returns the cancellation token for <paramref name="taskId"/>, creating a new source
    /// linked to <paramref name="workerToken"/> if none exists yet. A task stopped while it was still
    /// queued gets an already-cancelled token.
    /// </summary>
    CancellationToken Acquire(Guid taskId, CancellationToken workerToken, Guid? chatId = null);

    /// <summary>Cancels the source registered for <paramref name="taskId"/>, if any.</summary>
    void Cancel(Guid taskId);

    /// <summary>
    /// Stops a turn wherever it is: cancels it when running, or marks it so the worker skips it when
    /// it is still queued (<paramref name="isQueued"/> tells whether the queue still holds it).
    /// </summary>
    StopOutcome Stop(Guid taskId, bool isQueued);

    /// <summary>True when the turn was stopped before the worker picked it up.</summary>
    bool WasStoppedWhileQueued(Guid taskId);

    /// <summary>Cancels every running turn of a chat (the chat is being deleted). Returns how many.</summary>
    int CancelChat(Guid chatId);

    /// <summary>Removes and disposes the source registered for <paramref name="taskId"/>.</summary>
    void Release(Guid taskId);
}

public class ConexyCancellationRegistry : IConexyCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, (CancellationTokenSource Source, Guid? ChatId)> _sources = new();
    // STOP_CONFIRM: ids stopped while still in the queue. Raw ids only, consumed by the worker.
    private readonly ConcurrentDictionary<Guid, byte> _stoppedWhileQueued = new();
    private readonly ILogger<ConexyCancellationRegistry> _logger;

    public ConexyCancellationRegistry(ILogger<ConexyCancellationRegistry> logger)
    {
        _logger = logger;
    }

    public CancellationToken Acquire(Guid taskId, CancellationToken workerToken, Guid? chatId = null)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(workerToken);
        var entry = _sources.GetOrAdd(taskId, (linked, chatId));
        if (!ReferenceEquals(entry.Source, linked))
        {
            // The queue guard guarantees a single in-flight job per task, so this should
            // not normally happen — just drop the redundant linked source.
            linked.Dispose();
        }

        if (_stoppedWhileQueued.ContainsKey(taskId))
        {
            entry.Source.Cancel();
        }

        return entry.Source.Token;
    }

    public void Cancel(Guid taskId)
    {
        if (_sources.TryGetValue(taskId, out var entry))
        {
            _logger.LogInformation("Cancelling generation for task {TaskId}.", taskId);
            entry.Source.Cancel();
        }
        else
        {
            _logger.LogInformation("Stop requested for task {TaskId} but no active generation was found.", taskId);
        }
    }

    public StopOutcome Stop(Guid taskId, bool isQueued)
    {
        if (_sources.TryGetValue(taskId, out var entry))
        {
            _logger.LogInformation("Cancelling generation for task {TaskId}.", taskId);
            entry.Source.Cancel();
            return StopOutcome.Stopping;
        }

        if (isQueued)
        {
            _logger.LogInformation("Task {TaskId} stopped while still queued; the worker will skip it.", taskId);
            _stoppedWhileQueued[taskId] = 0;
            // The worker may have picked the task up in between: cancel the fresh source as well.
            if (_sources.TryGetValue(taskId, out var late))
            {
                late.Source.Cancel();
                return StopOutcome.Stopping;
            }
            return StopOutcome.Cancelled;
        }

        _logger.LogInformation("Stop requested for task {TaskId} but nothing is in flight.", taskId);
        return StopOutcome.NotFound;
    }

    public bool WasStoppedWhileQueued(Guid taskId) => _stoppedWhileQueued.ContainsKey(taskId);

    public int CancelChat(Guid chatId)
    {
        var cancelled = 0;
        foreach (var (taskId, entry) in _sources)
        {
            if (entry.ChatId != chatId) continue;
            _logger.LogInformation("Cancelling task {TaskId}: its chat {ChatId} is being deleted.", taskId, chatId);
            entry.Source.Cancel();
            cancelled++;
        }
        return cancelled;
    }

    public void Release(Guid taskId)
    {
        _stoppedWhileQueued.TryRemove(taskId, out _);
        if (_sources.TryRemove(taskId, out var entry))
        {
            entry.Source.Dispose();
        }
    }
}
