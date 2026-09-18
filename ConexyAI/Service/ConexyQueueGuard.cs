using System.Collections.Concurrent;
using ConexyAI.Contract;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

/// <summary>
/// Idempotency wrapper around the in-memory <see cref="IConexyQueue"/>. Prevents two
/// concurrent requests with the same <c>SessionId</c> (<see cref="ConexyJob.TaskId"/>)
/// from enqueueing the agent task twice. The flag is released only after the worker
/// finishes processing the job (see <see cref="MarkCompleted"/>), so it also covers
/// the "already executing" window.
/// </summary>
public interface IConexyQueueGuard
{
    /// <summary>Enqueues <paramref name="job"/> unless a task with the same id is already queued/in-flight.</summary>
    /// <returns><c>true</c> if the job was enqueued, <c>false</c> if it was skipped as a duplicate.</returns>
    Task<bool> EnqueueIfNotInFlightAsync(ConexyJob job, CancellationToken ct = default);

    /// <summary>Releases the in-flight guard for <paramref name="taskId"/> after its processing finished.</summary>
    void MarkCompleted(Guid taskId);
}

public class ConexyQueueGuard : IConexyQueueGuard
{
    private readonly IConexyQueue _queue;
    private readonly ILogger<ConexyQueueGuard> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    public ConexyQueueGuard(IConexyQueue queue, ILogger<ConexyQueueGuard> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public async Task<bool> EnqueueIfNotInFlightAsync(ConexyJob job, CancellationToken ct = default)
    {
        if (!_inFlight.TryAdd(job.TaskId, 0))
        {
            _logger.LogInformation("Session {SessionId} already enqueued/in-flight, skipping duplicate enqueue.", job.TaskId);
            return false;
        }

        try
        {
            await _queue.EnqueueAsync(job, ct);
            return true;
        }
        catch
        {
            _inFlight.TryRemove(job.TaskId, out _);
            throw;
        }
    }

    public void MarkCompleted(Guid taskId)
    {
        _inFlight.TryRemove(taskId, out _);
    }
}
