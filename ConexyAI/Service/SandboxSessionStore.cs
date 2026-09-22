using ConexyAI.Configuration;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// SANDBOX_SESSIONS: добавлено 2026-09-23
/// <summary>
/// Tracks one sandbox state directory per agent session (chat) and when it was last used.
/// <para>
/// Why state instead of a long-lived container: the sandbox command path deliberately runs a
/// throwaway container per command (see <see cref="DockerSandboxRunner"/>) because the
/// docker-socket-proxy in front of the daemon denies <c>/exec/*</c> (<c>EXEC=0</c> in
/// docker-compose.prod.yml) and the proxy cannot carry Docker's hijacked attach stream reliably.
/// A persistent container would therefore need either a weakened proxy ACL or a working attach
/// channel. Keeping the container throwaway and persisting the parts that actually matter — HOME,
/// package-manager caches, installed tooling under <c>/state</c> — gives the agent continuity
/// across commands without either compromise, and this store is what expires that state.
/// </para>
/// </summary>
public interface ISandboxSessionStore
{
    /// <summary>
    /// Host directory backing this session's sandbox state, created on first use and marked as
    /// activity. Mounted into the sandbox container at <c>/state</c>.
    /// </summary>
    string GetOrCreateStatePath(Guid sessionId);

    /// <summary>Records that the session just ran something, so the idle sweeper leaves it alone.</summary>
    void Touch(Guid sessionId);

    /// <summary>
    /// Drops every session idle for longer than <paramref name="timeout"/> from the registry and
    /// returns what was dropped, so the caller can delete the directories (and log the reason).
    /// </summary>
    IReadOnlyList<SandboxSessionSnapshot> CollectIdle(TimeSpan timeout);

    /// <summary>Number of sessions currently held (for the sweeper's periodic summary log).</summary>
    int ActiveSessionCount { get; }
}

/// <summary>One expired session: what to delete and how long it had been idle.</summary>
public sealed record SandboxSessionSnapshot(Guid SessionId, string StatePath, TimeSpan IdleFor);

public sealed class SandboxSessionStore : ISandboxSessionStore
{
    private sealed class Entry
    {
        public required string Path { get; init; }
        public DateTime LastActivityUtc { get; set; }
    }

    private readonly Dictionary<Guid, Entry> _sessions = [];
    private readonly object _gate = new();
    private readonly IOptions<SandboxOptions> _options;
    private readonly ILogger<SandboxSessionStore> _logger;

    public SandboxSessionStore(IOptions<SandboxOptions> options, ILogger<SandboxSessionStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    public int ActiveSessionCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    public string GetOrCreateStatePath(Guid sessionId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
            {
                existing.LastActivityUtc = DateTime.UtcNow;
                return existing.Path;
            }

            var path = PathFor(sessionId);
            EnsureDirectory(path, "home");
            EnsureDirectory(path, ".npm");
            EnsureDirectory(path, ".cache");
            EnsureDirectory(path, ".local");
            RelaxPermissions(path);

            _sessions[sessionId] = new Entry { Path = path, LastActivityUtc = DateTime.UtcNow };
            _logger.LogInformation(
                "Sandbox session opened: session={SessionId} path={Path} active={Active}",
                sessionId, path, _sessions.Count);
            return path;
        }
    }

    public void Touch(Guid sessionId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var entry))
            {
                entry.LastActivityUtc = DateTime.UtcNow;
            }
        }
    }

    public IReadOnlyList<SandboxSessionSnapshot> CollectIdle(TimeSpan timeout)
    {
        var now = DateTime.UtcNow;
        var expired = new List<SandboxSessionSnapshot>();

        lock (_gate)
        {
            foreach (var (sessionId, entry) in _sessions.ToList())
            {
                var idle = now - entry.LastActivityUtc;
                if (idle < timeout) continue;

                _sessions.Remove(sessionId);
                expired.Add(new SandboxSessionSnapshot(sessionId, entry.Path, idle));
            }
        }

        return expired;
    }

    private string PathFor(Guid sessionId) =>
        Path.Combine(_options.Value.StateRootPath, sessionId.ToString("N"));

    /// <summary>
    /// Creates a state sub-directory and widens its permissions. The sandbox runs as a different
    /// user inside its own container, and the backend creates these directories as its own user, so
    /// a 0755 directory would not be writable there. The directory is per session and is the only
    /// thing that container mounts, so relaxing it exposes nothing else.
    /// </summary>
    private void EnsureDirectory(string parent, string child)
    {
        var path = Path.Combine(parent, child);
        Directory.CreateDirectory(path);
        RelaxPermissions(path);
    }

    /// <summary>Widens a state directory to 0777 so the sandbox user can write into it.</summary>
    private void RelaxPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not relax permissions on sandbox state directory {Path}", path);
        }
    }
}
