using System.Collections.Concurrent;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Hub;
using ConexyAI.Repository;
using Microsoft.AspNetCore.SignalR;

namespace ConexyAI.Service.Device;

// WEB_DEVICE: добавлено 2026-10-18 — задачи «на моём ПК», отправленные из веба, пока ПК офлайн.
// Очередь в памяти процесса (как и основная IConexyQueue): задание несёт креды (GitHub/MCP), которые
// нельзя хранить в БД. Строка задачи при этом существует (Pending), а id остаётся зарезервированным
// в IConexyQueueGuard — чат «занят» до отправки/отмены. После рестарта строку закрывает
// RecoverOrphanedTasksAsync воркера, как любую Pending.

/// <summary>Задача, ждущая подключения ПК.</summary>
public sealed record QueuedDeviceTask(ConexyJob Job, DateTime CreatedAt);

public interface IDeviceTaskQueue
{
    /// <summary>Ставит задачу в ожидание (id уже зарезервирован в <see cref="IConexyQueueGuard"/>).</summary>
    void Hold(ConexyJob job);

    IReadOnlyList<QueuedDeviceTask> List(Guid userId);

    bool Contains(Guid taskId);

    /// <summary>ПК пользователя подключился — все его ожидающие задачи уходят в рабочую очередь.</summary>
    Task<int> DispatchAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Перевести ожидающую задачу в песочницу. False — задачи нет или она чужая.</summary>
    Task<bool> MoveToSandboxAsync(Guid userId, Guid taskId, CancellationToken ct = default);

    /// <summary>Отменить ожидающую задачу (строка Cancelled + OnStopped). False — задачи нет или она чужая.</summary>
    Task<bool> CancelAsync(Guid userId, Guid taskId, CancellationToken ct = default);
}

public sealed class DeviceTaskQueue : IDeviceTaskQueue
{
    private readonly ConcurrentDictionary<Guid, QueuedDeviceTask> _tasks = new();
    private readonly IConexyQueueGuard _queueGuard;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ConexyHub> _hub;
    private readonly ILogger<DeviceTaskQueue> _logger;

    public DeviceTaskQueue(
        IConexyQueueGuard queueGuard,
        IServiceScopeFactory scopeFactory,
        IHubContext<ConexyHub> hub,
        ILogger<DeviceTaskQueue> logger)
    {
        _queueGuard = queueGuard;
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    public void Hold(ConexyJob job)
    {
        _tasks[job.TaskId] = new QueuedDeviceTask(job, DateTime.UtcNow);
        _logger.LogInformation("Task {TaskId} is waiting for the user's device (user={UserId}).", job.TaskId, job.UserId);
    }

    public IReadOnlyList<QueuedDeviceTask> List(Guid userId) =>
        _tasks.Values.Where(t => t.Job.UserId == userId).OrderBy(t => t.CreatedAt).ToList();

    public bool Contains(Guid taskId) => _tasks.ContainsKey(taskId);

    public async Task<int> DispatchAsync(Guid userId, CancellationToken ct = default)
    {
        var dispatched = 0;
        foreach (var queued in List(userId))
        {
            if (!TryTake(userId, queued.Job.TaskId, out var job))
                continue;
            if (await EnqueueAsync(job, ct))
                dispatched++;
        }

        if (dispatched > 0)
            _logger.LogInformation("Dispatched {Count} queued device task(s) for user {UserId}.", dispatched, userId);
        return dispatched;
    }

    public async Task<bool> MoveToSandboxAsync(Guid userId, Guid taskId, CancellationToken ct = default)
    {
        if (!TryTake(userId, taskId, out var job))
            return false;

        await EnqueueAsync(job with
        {
            UseDevice = false,
            DeviceTools = null,
            DeviceProjectId = null,
            RemoteApprovals = false,
        }, ct);
        return true;
    }

    public async Task<bool> CancelAsync(Guid userId, Guid taskId, CancellationToken ct = default)
    {
        if (!TryTake(userId, taskId, out _))
            return false;

        _queueGuard.MarkCompleted(taskId);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IConexyRepository>();
            var entity = await repository.GetByIdAsync(taskId, CancellationToken.None);
            if (entity is not null && entity.Status is ConexyStatus.Pending or ConexyStatus.Running)
            {
                entity.Status = ConexyStatus.Cancelled;
                entity.Result ??= "Generation stopped.";
                entity.FinishedAt = DateTime.UtcNow;
                await repository.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not close queued device task {TaskId}.", taskId);
        }

        try { await _hub.Clients.Group($"task_{taskId}").SendAsync("OnStopped", taskId, CancellationToken.None); } catch { }
        return true;
    }

    private bool TryTake(Guid userId, Guid taskId, out ConexyJob job)
    {
        job = null!;
        if (!_tasks.TryGetValue(taskId, out var queued) || queued.Job.UserId != userId)
            return false;
        if (!_tasks.TryRemove(new KeyValuePair<Guid, QueuedDeviceTask>(taskId, queued)))
            return false;
        job = queued.Job;
        return true;
    }

    // Резервация id держится всё время ожидания; EnqueueReservedAsync при сбое снимает только id,
    // поэтому освобождаем и чат — иначе он остался бы «занятым» навсегда.
    private async Task<bool> EnqueueAsync(ConexyJob job, CancellationToken ct)
    {
        try
        {
            await _queueGuard.EnqueueReservedAsync(job, ct);
            return true;
        }
        catch (Exception ex)
        {
            _queueGuard.MarkCompleted(job.TaskId);
            _logger.LogError(ex, "Failed to enqueue device task {TaskId}.", job.TaskId);
            try { await _hub.Clients.Group($"task_{job.TaskId}").SendAsync("OnError", "Не удалось запустить задачу.", CancellationToken.None); } catch { }
            return false;
        }
    }
}
