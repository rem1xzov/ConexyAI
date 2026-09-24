using System.Collections.Concurrent;

namespace ConexyAI.Service;

// WORKSPACE_JAIL: добавлено 2026-09-24 — ревью C2.
/// <summary>
/// Knows which chat workspaces have a sandbox command running right now, and serializes commands
/// per workspace across scopes (the bash service is scoped, so its own lock dictionary never covered
/// two scopes at once).
/// <para>
/// A command inside the sandbox can swap a directory for a symlink at any moment. Reads and writes
/// are safe against that (the jail verifies the opened descriptor), but deletes, renames and
/// extraction work on paths — so the file-explorer endpoints refuse those while a command runs in
/// the same workspace instead of racing it.
/// </para>
/// </summary>
public interface ISandboxActivity
{
    /// <summary>Waits for the workspace's command slot and holds it until the returned handle is disposed.</summary>
    Task<IDisposable> AcquireAsync(Guid chatId, CancellationToken ct = default);

    /// <summary>Takes the slot only if it is free right now; null when a command is already running.</summary>
    IDisposable? TryAcquire(Guid chatId);

    /// <summary>True while a sandbox command runs in this chat's workspace.</summary>
    bool IsBusy(Guid chatId);
}

public class SandboxActivity : ISandboxActivity
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _slots = new();

    public async Task<IDisposable> AcquireAsync(Guid chatId, CancellationToken ct = default)
    {
        var slot = _slots.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await slot.WaitAsync(ct);
        return new Release(slot);
    }

    public IDisposable? TryAcquire(Guid chatId)
    {
        var slot = _slots.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        return slot.Wait(0) ? new Release(slot) : null;
    }

    public bool IsBusy(Guid chatId) =>
        _slots.TryGetValue(chatId, out var slot) && slot.CurrentCount == 0;

    private sealed class Release : IDisposable
    {
        private SemaphoreSlim? _slot;

        public Release(SemaphoreSlim slot) => _slot = slot;

        public void Dispose() => Interlocked.Exchange(ref _slot, null)?.Release();
    }
}

/// <summary>Thrown by file operations refused while a sandbox command runs in the workspace. Mapped to 409.</summary>
public sealed class WorkspaceBusyException : InvalidOperationException
{
    public WorkspaceBusyException()
        : base("A command is running in this workspace; try again when it finishes.")
    {
    }
}
