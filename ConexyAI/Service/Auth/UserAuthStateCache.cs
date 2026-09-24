using Microsoft.Extensions.Caching.Memory;

namespace ConexyAI.Service.Auth;

// TOKEN_REVOCATION: добавлено 2026-09-24 (ревью M19)
/// <summary>The slice of a user row every authenticated request is checked against.</summary>
public sealed record UserAuthState(int TokenVersion, bool IsAdmin);

/// <summary>
/// Short-lived per-process cache of <see cref="UserAuthState"/>, so the per-request revocation
/// check costs one primary-key lookup per user per <see cref="Ttl"/> instead of one per request.
/// <c>UserRepository</c> invalidates an entry whenever it writes or deletes that user, so a
/// logout / demotion / deletion is seen by the very next request of this process; the TTL only
/// bounds staleness for writes made elsewhere (another process, raw SQL).
/// </summary>
public interface IUserAuthStateCache
{
    ValueTask<UserAuthState?> GetOrLoadAsync(
        Guid userId,
        Func<CancellationToken, Task<UserAuthState?>> loader,
        CancellationToken ct = default);

    void Invalidate(Guid userId);
}

public sealed class UserAuthStateCache : IUserAuthStateCache, IDisposable
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    // Отдельный экземпляр MemoryCache, а не общий IMemoryCache из DI: свой SizeLimit и никакой
    // зависимости от того, как общий кэш настроят другие части приложения.
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100_000 });

    // Гонка «загрузили старое → параллельно инвалидировали → положили старое в кэш» закрыта
    // счётчиком поколений: результат загрузки кладётся в кэш, только если за время загрузки не было
    // ни одной инвалидации. Инвалидации редки (логин/логаут/админ-действия), так что лишний промах
    // кэша после них ничего не стоит.
    private long _generation;

    public async ValueTask<UserAuthState?> GetOrLoadAsync(
        Guid userId,
        Func<CancellationToken, Task<UserAuthState?>> loader,
        CancellationToken ct = default)
    {
        if (_cache.TryGetValue(userId, out UserAuthState? cached) && cached is not null)
            return cached;

        var generation = Interlocked.Read(ref _generation);
        var loaded = await loader(ct);

        // A missing user is never cached: a deleted user's token then costs one PK lookup per
        // request, and an id created later (dev-token) is never shadowed by a stale "missing".
        if (loaded is not null && Interlocked.Read(ref _generation) == generation)
        {
            _cache.Set(userId, loaded, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = Ttl,
                Size = 1
            });
        }

        return loaded;
    }

    public void Invalidate(Guid userId)
    {
        Interlocked.Increment(ref _generation);
        _cache.Remove(userId);
    }

    public void Dispose() => _cache.Dispose();
}
