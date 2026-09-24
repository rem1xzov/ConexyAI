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

    // TURN_IN_FLIGHT: добавлено 2026-09-24 — ревью H6.
    /// <summary>
    /// Reserves <paramref name="taskId"/> before the caller touches the task row. False when a turn
    /// with that id is already queued or running — the caller must answer 409 instead of silently
    /// dropping the request (which used to overwrite the running task's row and return 202).
    /// </summary>
    bool TryReserve(Guid taskId);

    /// <summary>Enqueues a job whose id was reserved with <see cref="TryReserve"/>; releases it on failure.</summary>
    Task EnqueueReservedAsync(ConexyJob job, CancellationToken ct = default);

    /// <summary>True while a turn with this id is queued or running.</summary>
    bool IsInFlight(Guid taskId);
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

    public bool TryReserve(Guid taskId) => _inFlight.TryAdd(taskId, 0);

    public async Task EnqueueReservedAsync(ConexyJob job, CancellationToken ct = default)
    {
        try
        {
            await _queue.EnqueueAsync(job, ct);
        }
        catch
        {
            _inFlight.TryRemove(job.TaskId, out _);
            throw;
        }
    }

    public bool IsInFlight(Guid taskId) => _inFlight.ContainsKey(taskId);
}
