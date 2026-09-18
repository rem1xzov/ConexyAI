using System.Collections.Concurrent;

namespace ConexyAI.Service;

/// <summary>A point-in-time snapshot of a file before an agent edit, used for undo.</summary>
public record FileSnapshot(string Path, string? ContentBefore, bool ExistedBefore);

/// <summary>
/// Cross-cutting, per-session editor state shared between the agent's
/// <see cref="ConexyEditorService"/> (Scoped) and the file explorer REST API (Singleton).
/// Keeping undo stacks and per-path write locks here lets a user's manual save/delete/rename
/// invalidate the agent's undo history and serialize against concurrent agent edits.
/// </summary>
public interface IConexyEditorStateService
{
    void PushUndo(Guid sessionId, string path, FileSnapshot snapshot);
    bool TryPopUndo(Guid sessionId, string path, out FileSnapshot snapshot);
    void ClearUndo(Guid sessionId, string path);
    void ClearSession(Guid sessionId);

    /// <summary>Returns (and lazily creates) the per-path write lock for a session.</summary>
    SemaphoreSlim GetLock(Guid sessionId, string path);

    /// <summary>Runs <paramref name="action"/> while holding the per-path lock.</summary>
    Task<T> WithLockAsync<T>(Guid sessionId, string path, Func<Task<T>> action);

    /// <summary>Runs a void-returning <paramref name="action"/> while holding the per-path lock.</summary>
    Task WithLockAsync(Guid sessionId, string path, Func<Task> action);
}

public class ConexyEditorStateService : IConexyEditorStateService
{
    private readonly ConcurrentDictionary<(Guid SessionId, string Path), Stack<FileSnapshot>> _undoStacks = new();
    private readonly ConcurrentDictionary<(Guid SessionId, string Path), SemaphoreSlim> _pathLocks = new();

    public void PushUndo(Guid sessionId, string path, FileSnapshot snapshot)
    {
        var stack = _undoStacks.GetOrAdd((sessionId, path), _ => new Stack<FileSnapshot>());
        lock (stack)
        {
            stack.Push(snapshot);
        }
    }

    public bool TryPopUndo(Guid sessionId, string path, out FileSnapshot snapshot)
    {
        if (!_undoStacks.TryGetValue((sessionId, path), out var stack))
        {
            snapshot = null!;
            return false;
        }

        lock (stack)
        {
            if (stack.Count == 0)
            {
                snapshot = null!;
                return false;
            }

            snapshot = stack.Pop();
            return true;
        }
    }

    public void ClearUndo(Guid sessionId, string path)
    {
        if (_undoStacks.TryGetValue((sessionId, path), out var stack))
        {
            lock (stack)
            {
                stack.Clear();
            }
        }
    }

    public void ClearSession(Guid sessionId)
    {
        foreach (var key in _undoStacks.Keys)
        {
            if (key.SessionId == sessionId)
            {
                _undoStacks.TryRemove(key, out _);
            }
        }

        foreach (var key in _pathLocks.Keys)
        {
            if (key.SessionId == sessionId)
            {
                _pathLocks.TryRemove(key, out _);
            }
        }
    }

    public SemaphoreSlim GetLock(Guid sessionId, string path) =>
        _pathLocks.GetOrAdd((sessionId, path), _ => new SemaphoreSlim(1, 1));

    public async Task<T> WithLockAsync<T>(Guid sessionId, string path, Func<Task<T>> action)
    {
        var semaphore = GetLock(sessionId, path);
        await semaphore.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task WithLockAsync(Guid sessionId, string path, Func<Task> action)
    {
        var semaphore = GetLock(sessionId, path);
        await semaphore.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            semaphore.Release();
        }
    }
}
