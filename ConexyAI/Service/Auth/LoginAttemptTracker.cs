using Microsoft.Extensions.Caching.Memory;

namespace ConexyAI.Service.Auth;

// LOGIN_LOCKOUT: добавлено 2026-09-24 (ревью M20)
/// <summary>
/// Per-account failed-login lockout, keyed by the NORMALIZED email. Failures for an email that has
/// no account count exactly like failures for a real one, so the lockout never reveals whether an
/// account exists.
/// </summary>
public interface ILoginAttemptTracker
{
    /// <summary>Remaining lockout time for <paramref name="normalizedEmail"/>, or <c>null</c> when not locked.</summary>
    TimeSpan? GetLockout(string normalizedEmail);

    /// <summary>
    /// Records one failed attempt. Returns the lockout duration when this failure triggered a
    /// lockout, otherwise <c>null</c>.
    /// </summary>
    TimeSpan? RegisterFailure(string normalizedEmail);

    /// <summary>Forgets the failures of <paramref name="normalizedEmail"/> (call after a successful login).</summary>
    void Reset(string normalizedEmail);
}

public sealed class LoginAttemptTracker : ILoginAttemptTracker, IDisposable
{
    /// <summary>Failures within <see cref="FailureWindow"/> that lock the account.</summary>
    public const int MaxFailures = 5;

    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private sealed record Entry(int Failures, DateTimeOffset WindowStart, DateTimeOffset? LockedUntil);

    private readonly TimeProvider _time;
    private readonly object _sync = new();
    // Отдельный ограниченный кэш: перебор случайных email не раздует память (плюс per-IP лимит на
    // эндпоинте ограничивает скорость появления новых ключей).
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100_000 });

    public LoginAttemptTracker() : this(TimeProvider.System)
    {
    }

    public LoginAttemptTracker(TimeProvider time)
    {
        _time = time;
    }

    public TimeSpan? GetLockout(string normalizedEmail)
    {
        lock (_sync)
        {
            if (!_cache.TryGetValue(normalizedEmail, out Entry? entry) || entry?.LockedUntil is not { } until)
                return null;

            var remaining = until - _time.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : null;
        }
    }

    public TimeSpan? RegisterFailure(string normalizedEmail)
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            _cache.TryGetValue(normalizedEmail, out Entry? entry);

            // A new window starts after the previous one lapsed or after a served lockout.
            if (entry is null
                || now - entry.WindowStart >= FailureWindow
                || (entry.LockedUntil is { } until && until <= now))
            {
                entry = new Entry(0, now, null);
            }

            entry = entry with { Failures = entry.Failures + 1 };
            TimeSpan? triggered = null;
            if (entry.LockedUntil is null && entry.Failures >= MaxFailures)
            {
                entry = entry with { LockedUntil = now + LockoutDuration };
                triggered = LockoutDuration;
            }

            // Cache expiry is only housekeeping (the logic above works on the stored timestamps):
            // keep the entry for as long as either the window or the lockout can still matter.
            _cache.Set(normalizedEmail, entry, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = FailureWindow + LockoutDuration,
                Size = 1
            });

            return triggered;
        }
    }

    public void Reset(string normalizedEmail)
    {
        lock (_sync)
        {
            _cache.Remove(normalizedEmail);
        }
    }

    public void Dispose() => _cache.Dispose();
}
