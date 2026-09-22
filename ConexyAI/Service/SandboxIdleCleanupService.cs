using ConexyAI.Configuration;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// SANDBOX_SESSIONS: добавлено 2026-09-23
/// <summary>
/// Deletes a session's sandbox state once it has been idle for
/// <see cref="SandboxOptions.IdleTimeoutMinutes"/> (10 minutes by default).
/// <para>
/// The containers themselves are already throwaway — one per command, removed as soon as the
/// command exits — so what accumulates between commands is only the session state directory
/// (installed tooling, package caches, HOME). This sweeper is what stops that from piling up.
/// </para>
/// </summary>
public sealed class SandboxIdleCleanupService : BackgroundService
{
    private readonly ISandboxSessionStore _sessions;
    private readonly IOptions<SandboxOptions> _options;
    private readonly ILogger<SandboxIdleCleanupService> _logger;

    public SandboxIdleCleanupService(
        ISandboxSessionStore sessions,
        IOptions<SandboxOptions> options,
        ILogger<SandboxIdleCleanupService> logger)
    {
        _sessions = sessions;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromMinutes(_options.Value.IdleTimeoutMinutes);
        if (timeout <= TimeSpan.Zero)
        {
            _logger.LogInformation("Sandbox idle cleanup is disabled (Sandbox:IdleTimeoutMinutes <= 0).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.Value.IdleSweepIntervalSeconds));
        _logger.LogInformation(
            "Sandbox idle cleanup started: sweep every {Interval}s, delete state after {Timeout} of inactivity.",
            interval.TotalSeconds, timeout);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                Sweep(timeout);
            }
            catch (Exception ex)
            {
                // A failed sweep must never take the worker down.
                _logger.LogError(ex, "Sandbox idle sweep failed.");
            }
        }
    }

    private void Sweep(TimeSpan timeout)
    {
        var expired = _sessions.CollectIdle(timeout);
        if (expired.Count == 0)
        {
            return;
        }

        foreach (var session in expired)
        {
            try
            {
                if (Directory.Exists(session.StatePath))
                {
                    Directory.Delete(session.StatePath, recursive: true);
                }

                _logger.LogInformation(
                    "Sandbox session reaped after {IdleMinutes:0.#} min idle: session={SessionId} path={Path} remaining={Remaining}",
                    session.IdleFor.TotalMinutes, session.SessionId, session.StatePath, _sessions.ActiveSessionCount);
            }
            catch (Exception ex)
            {
                // The container is already gone by this point, so a leftover directory is only
                // wasted disk, not a security problem — report it and move on.
                _logger.LogWarning(
                    ex,
                    "Could not delete sandbox state for session {SessionId} at {Path}.",
                    session.SessionId, session.StatePath);
            }
        }
    }
}
