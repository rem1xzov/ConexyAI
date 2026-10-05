using System.Collections.Concurrent;
using System.Text;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Hub;
using ConexyAI.Service.Pty;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// MULTI_TERMINAL: обновлено 2026-10-05 — терминалов на чат теперь несколько, поэтому сессия pty
// адресуется парой (chatId, terminalId). Пустой id трактуется как "main" (первый терминал), туда же
// попадает зеркальный вывод агента.
public static class TerminalIds
{
    public const string Main = "main";

    /// <summary>Normalizes a client-supplied terminal id into a bounded, safe dictionary key.</summary>
    public static string Normalize(string? terminalId)
    {
        if (string.IsNullOrWhiteSpace(terminalId)) return Main;
        var trimmed = terminalId.Trim();
        if (trimmed.Length > 40) trimmed = trimmed[..40];
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.') builder.Append(ch);
        }
        return builder.Length == 0 ? Main : builder.ToString();
    }
}

public interface IIdeTerminalService
{
    /// <summary>Creates (or reuses) an interactive shell for the session's terminal id and streams output.</summary>
    Task StartAsync(Guid sessionId, string? terminalId = null, CancellationToken ct = default);

    /// <summary>Writes keystrokes to the terminal's stdin.</summary>
    Task SendInputAsync(Guid sessionId, string? terminalId, string data, CancellationToken ct = default);

    /// <summary>Updates the terminal window size.</summary>
    Task ResizeAsync(Guid sessionId, string? terminalId, int cols, int rows, CancellationToken ct = default);

    /// <summary>Explicitly closes one terminal shell and releases its pty.</summary>
    Task StopAsync(Guid sessionId, string? terminalId = null, CancellationToken ct = default);

    /// <summary>Closes every active terminal (used when the agent session finishes).</summary>
    void StopAll();

    /// <summary>Number of active pty sessions (exposed for tests and diagnostics).</summary>
    int ActiveSessionCount { get; }
}

/// <summary>
/// Manages one long-lived interactive pty shell per (session, terminal). Deliberately separate from the
/// one-shot <see cref="ConexyBashService"/> processes: both target the same workspace directory
/// but are distinct OS processes.
/// </summary>
public class IdeTerminalService : IIdeTerminalService
{
    private readonly IConexyWorkspaceService _workspaceService;
    private readonly IHubContext<ConexyHub> _hubContext;
    private readonly ILogger<IdeTerminalService> _logger;
    // RUN_CRASH: добавлено 2026-09-23 — шелл живёт вне песочницы, поэтому только для разработки.
    private readonly bool _allowBackendShell;

    private readonly ConcurrentDictionary<(Guid SessionId, string TerminalId), TerminalSession> _sessions = new();

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    public IdeTerminalService(
        IConexyWorkspaceService workspaceService,
        IHubContext<ConexyHub> hubContext,
        IOptions<SandboxOptions> sandboxOptions,
        ILogger<IdeTerminalService> logger)
    {
        _workspaceService = workspaceService;
        _hubContext = hubContext;
        _allowBackendShell = sandboxOptions.Value.AllowBackendShell;
        _logger = logger;
    }

    /// <summary>
    /// RUN_CRASH: shown when the backend shell is disabled (see <see cref="SandboxOptions.AllowBackendShell"/>).
    /// </summary>
    public const string BackendShellDisabledMessage =
        "Интерактивный терминал отключён: он выполняется вне песочницы, на самом сервере.";

    public int ActiveSessionCount => _sessions.Count;

    public Task StartAsync(Guid sessionId, string? terminalId = null, CancellationToken ct = default)
    {
        var id = TerminalIds.Normalize(terminalId);

        // RUN_CRASH: добавлено 2026-09-23 — хаб-метод StartTerminal доступен любому вошедшему
        // пользователю (без проверки владельца sessionId), а шелл получает окружение бэкенда и
        // доступ к docker-socket-proxy. В проде такой шелл — это захват хоста, поэтому он закрыт.
        // SendInput/Resize без сессии ничего не делают, так что закрыть достаточно здесь.
        if (!_allowBackendShell)
        {
            _logger.LogWarning("Refused to start a backend shell for session {SessionId}: Sandbox:AllowBackendShell is off.", sessionId);
            throw new InvalidOperationException(BackendShellDisabledMessage);
        }

        var key = (sessionId, id);
        // Reconnect after a page reload: reuse the existing shell instead of spawning another.
        if (_sessions.TryGetValue(key, out var existing) && !existing.IsStopped)
        {
            _logger.LogInformation("Terminal {TerminalId} for session {SessionId} already running; reusing.", id, sessionId);
            return Task.CompletedTask;
        }

        var workspacePath = _workspaceService.GetTaskWorkspacePath(sessionId);
        var (app, args) = ResolveShell();

        IPtyConnection connection;
        try
        {
            connection = PtyProvider.Spawn(new PtyOptions
            {
                Name = "xterm-256color",
                Cols = 80,
                Rows = 24,
                WorkingDirectory = workspacePath,
                App = app,
                CommandLine = args
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spawn interactive shell for session {SessionId} terminal {TerminalId}.", sessionId, id);
            throw;
        }

        var session = new TerminalSession(key, connection, workspacePath, idleKey => _ = StopAsync(idleKey.SessionId, idleKey.TerminalId));
        if (!_sessions.TryAdd(key, session))
        {
            // Lost a race with a concurrent StartAsync: keep the winner, discard this one.
            connection.Dispose();
            session.Dispose();
            return Task.CompletedTask;
        }

        session.IdleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
        _ = Task.Run(() => ReadLoopAsync(key, session), CancellationToken.None);
        _logger.LogInformation("Started interactive terminal {TerminalId} for session {SessionId} (pid {Pid}).", id, sessionId, connection.ProcessId);
        return Task.CompletedTask;
    }

    public async Task SendInputAsync(Guid sessionId, string? terminalId, string data, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(data)) return;
        if (!_sessions.TryGetValue((sessionId, TerminalIds.Normalize(terminalId)), out var session) || session.IsStopped) return;

        var bytes = Encoding.UTF8.GetBytes(data);
        await session.WriteLock.WaitAsync(ct);
        try
        {
            // L12: only the size is logged — keystrokes can carry passwords and tokens.
            _logger.LogDebug("PTY write input [{SessionId}/{TerminalId}] {ByteCount} bytes", sessionId, session.TerminalId, bytes.Length);

            await session.Connection.InputStream.WriteAsync(bytes, ct);
            // The pty input stream may buffer small writes; flush so keystrokes reach the shell immediately.
            await session.Connection.InputStream.FlushAsync(ct);
            Touch(session);
        }
        finally
        {
            session.WriteLock.Release();
        }
    }

    public Task ResizeAsync(Guid sessionId, string? terminalId, int cols, int rows, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue((sessionId, TerminalIds.Normalize(terminalId)), out var session) && !session.IsStopped)
        {
            session.Connection.Resize(cols, rows);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(Guid sessionId, string? terminalId = null, CancellationToken ct = default)
    {
        if (!_sessions.TryRemove((sessionId, TerminalIds.Normalize(terminalId)), out var session)) return;
        StopSession(session);
        await Task.CompletedTask;
    }

    public void StopAll()
    {
        foreach (var key in _sessions.Keys.ToList())
        {
            if (_sessions.TryRemove(key, out var session))
            {
                StopSession(session);
            }
        }
    }

    private async Task ReadLoopAsync((Guid SessionId, string TerminalId) key, TerminalSession session)
    {
        _logger.LogInformation("ReadLoopAsync started for session {SessionId} terminal {TerminalId}", key.SessionId, key.TerminalId);

        try
        {
            var buffer = new byte[8192];
            var decoder = Encoding.UTF8.GetDecoder();

            while (true)
            {
                var read = await session.Connection.OutputStream.ReadAsync(buffer, session.ReadCts.Token);
                if (read == 0)
                {
                    // EOF: the shell exited.
                    _logger.LogInformation("ReadLoopAsync EOF for session {SessionId} terminal {TerminalId} (shell exited)", key.SessionId, key.TerminalId);
                    break;
                }

                Touch(session);

                var chars = new char[Encoding.UTF8.GetMaxCharCount(read)];
                var charCount = decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
                var text = new string(chars, 0, charCount);

                // L12: only the size — terminal output can carry secrets.
                _logger.LogDebug("PTY output [{SessionId}/{TerminalId}] {ByteCount} bytes", key.SessionId, key.TerminalId, read);

                await _hubContext.Clients.Group($"task_{key.SessionId}")
                    .SendAsync("TerminalOutput", new TerminalOutputEvent
                    {
                        SessionId = key.SessionId,
                        TerminalId = key.TerminalId,
                        Data = text
                    });
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ReadLoopAsync cancelled for session {SessionId} terminal {TerminalId}", key.SessionId, key.TerminalId);
        }
        catch (ObjectDisposedException)
        {
            _logger.LogInformation("ReadLoopAsync disposed for session {SessionId} terminal {TerminalId}", key.SessionId, key.TerminalId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReadLoopAsync failed for session {SessionId} terminal {TerminalId}", key.SessionId, key.TerminalId);
        }
        finally
        {
            // The shell exited (EOF) or the read failed: clean up the session entry.
            if (_sessions.TryRemove(key, out var removed) && ReferenceEquals(removed, session))
            {
                StopSession(session);
            }
        }
    }

    private static void Touch(TerminalSession session)
    {
        session.LastActivityUtc = DateTime.UtcNow;
        session.IdleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
    }

    private void StopSession(TerminalSession session)
    {
        if (Interlocked.Exchange(ref session._stoppedFlag, 1) != 0) return;

        try { session.ReadCts.Cancel(); } catch { /* best effort */ }
        try { session.IdleTimer.Dispose(); } catch { /* best effort */ }
        try { session.Connection.Dispose(); } catch { /* best effort */ }
        session.Dispose();

        _logger.LogInformation("Stopped interactive terminal {TerminalId} for session {SessionId}.", session.TerminalId, session.SessionId);
    }

    private static (string App, string[] Args) ResolveShell()
    {
        // Production target is Ubuntu: /bin/bash -i. On the Windows dev box, ConPTY hosts
        // Windows PowerShell so the terminal remains usable during local development.
        if (OperatingSystem.IsWindows())
        {
            // TEMP diagnostic: use cmd.exe instead of powershell.exe to test whether the
            // ConPTY silence is shell-specific. Revert to powershell.exe after diagnosis.
            return ("cmd.exe", Array.Empty<string>());
        }

        return ("/bin/bash", new[] { "-i" });
    }

    private sealed class TerminalSession : IDisposable
    {
        public Guid SessionId { get; }
        public string TerminalId { get; }
        public IPtyConnection Connection { get; }
        public string WorkspacePath { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public CancellationTokenSource ReadCts { get; } = new();
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
        public System.Threading.Timer IdleTimer { get; }
        public int _stoppedFlag;

        public bool IsStopped => Volatile.Read(ref _stoppedFlag) != 0;

        public TerminalSession((Guid SessionId, string TerminalId) key, IPtyConnection connection, string workspacePath, Action<(Guid SessionId, string TerminalId)> onIdle)
        {
            SessionId = key.SessionId;
            TerminalId = key.TerminalId;
            Connection = connection;
            WorkspacePath = workspacePath;

            IdleTimer = new System.Threading.Timer(
                _ =>
                {
                    if (DateTime.UtcNow - LastActivityUtc >= IdleTimeout)
                    {
                        onIdle(key);
                    }
                },
                state: null,
                dueTime: Timeout.InfiniteTimeSpan,
                period: Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            WriteLock.Dispose();
            ReadCts.Dispose();
        }
    }
}
