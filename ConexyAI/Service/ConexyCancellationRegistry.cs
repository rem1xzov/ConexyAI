using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

/// <summary>
/// Tracks a per-task <see cref="CancellationTokenSource"/> so an in-flight generation can
/// be cancelled on demand (the user's "Stop" button) without stopping the background worker
/// that owns the queue.
/// </summary>
public interface IConexyCancellationRegistry
{
    /// <summary>
    /// Returns the cancellation token for <paramref name="taskId"/>, creating a new source
    /// linked to <paramref name="workerToken"/> if none exists yet.
    /// </summary>
    CancellationToken Acquire(Guid taskId, CancellationToken workerToken);

    /// <summary>Cancels the source registered for <paramref name="taskId"/>, if any.</summary>
    void Cancel(Guid taskId);

    /// <summary>Removes and disposes the source registered for <paramref name="taskId"/>.</summary>
    void Release(Guid taskId);
}

public class ConexyCancellationRegistry : IConexyCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();
    private readonly ILogger<ConexyCancellationRegistry> _logger;

    public ConexyCancellationRegistry(ILogger<ConexyCancellationRegistry> logger)
    {
        _logger = logger;
    }

    public CancellationToken Acquire(Guid taskId, CancellationToken workerToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(workerToken);
        var cts = _sources.GetOrAdd(taskId, linked);
        if (!ReferenceEquals(cts, linked))
        {
            // The queue guard guarantees a single in-flight job per task, so this should
            // not normally happen — just drop the redundant linked source.
            linked.Dispose();
        }

        return cts.Token;
    }

    public void Cancel(Guid taskId)
    {
        if (_sources.TryGetValue(taskId, out var cts))
        {
            _logger.LogInformation("Cancelling generation for task {TaskId}.", taskId);
            cts.Cancel();
        }
        else
        {
            _logger.LogInformation("Stop requested for task {TaskId} but no active generation was found.", taskId);
        }
    }

    public void Release(Guid taskId)
    {
        if (_sources.TryRemove(taskId, out var cts))
        {
            cts.Dispose();
        }
    }
}
