using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Repository;

// PHONE_AGENT: добавлено 2026-10-29
public interface IMobilePushTokenRepository
{
    /// <summary>Максимум токенов на пользователя; при превышении вытесняется самый давний.</summary>
    const int MaxTokensPerUser = 10;

    /// <summary>Сохраняет токен (дедуп по значению; токен чужого пользователя переходит к текущему).</summary>
    Task UpsertAsync(Guid userId, string token, string platform, CancellationToken ct = default);

    /// <summary>Удаляет токен пользователя. False — такого токена у него нет.</summary>
    Task<bool> RemoveAsync(Guid userId, string token, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListTokensAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Удаляет недействительные токены (FCM UNREGISTERED).</summary>
    Task RemoveTokensAsync(IReadOnlyCollection<string> tokens, CancellationToken ct = default);
}

public sealed class MobilePushTokenRepository : IMobilePushTokenRepository
{
    private readonly DbConexy _db;

    public MobilePushTokenRepository(DbConexy db) => _db = db;

    public async Task UpsertAsync(Guid userId, string token, string platform, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var existing = await _db.MobilePushTokens.FirstOrDefaultAsync(t => t.Token == token, ct);
        if (existing is not null)
        {
            existing.UserId = userId;
            existing.Platform = platform;
            existing.LastSeenAt = now;
        }
        else
        {
            _db.MobilePushTokens.Add(new MobilePushTokenEntity
            {
                UserId = userId,
                Token = token,
                Platform = platform,
                CreatedAt = now,
                LastSeenAt = now,
            });
        }
        await _db.SaveChangesAsync(ct);

        var all = await _db.MobilePushTokens.Where(t => t.UserId == userId).ToListAsync(ct);
        var excess = all.Count - IMobilePushTokenRepository.MaxTokensPerUser;
        if (excess > 0)
        {
            var evict = all.Where(t => t.Token != token).OrderBy(t => t.LastSeenAt).Take(excess).ToList();
            _db.MobilePushTokens.RemoveRange(evict);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> RemoveAsync(Guid userId, string token, CancellationToken ct = default)
    {
        var row = await _db.MobilePushTokens.FirstOrDefaultAsync(t => t.UserId == userId && t.Token == token, ct);
        if (row is null)
            return false;
        _db.MobilePushTokens.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<string>> ListTokensAsync(Guid userId, CancellationToken ct = default) =>
        await _db.MobilePushTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.LastSeenAt)
            .Select(t => t.Token)
            .ToListAsync(ct);

    public async Task RemoveTokensAsync(IReadOnlyCollection<string> tokens, CancellationToken ct = default)
    {
        if (tokens.Count == 0)
            return;
        var rows = await _db.MobilePushTokens.Where(t => tokens.Contains(t.Token)).ToListAsync(ct);
        _db.MobilePushTokens.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
    }
}
