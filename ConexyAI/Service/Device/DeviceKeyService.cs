using System.Security.Cryptography;
using System.Text;
using ConexyAI.Contract;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Repository;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Service.Device;

// DESKTOP_AGENT: добавлено 2026-10-15
public sealed record DeviceKeyInfo(Guid Id, string Name, string KeyPrefix, DateTime CreatedAt, DateTime? LastUsedAt, bool Revoked);

public sealed record IssuedDeviceKey(DeviceKeyInfo Info, string Key);

public interface IDeviceKeyService
{
    /// <summary>Выпускает ключ. Открытый текст возвращается ровно один раз.</summary>
    Task<IssuedDeviceKey> IssueAsync(Guid userId, string? name, CancellationToken ct);

    Task<IReadOnlyList<DeviceKeyInfo>> ListAsync(Guid userId, CancellationToken ct);

    /// <summary>Отзывает ключ владельца. False — ключа нет или он чужой.</summary>
    Task<bool> RevokeAsync(Guid userId, Guid keyId, CancellationToken ct);

    /// <summary>Меняет действующий ключ на короткоживущий JWT владельца; null — ключ неверный или отозван.</summary>
    Task<TokenResponse?> ExchangeAsync(string key, CancellationToken ct);
}

public sealed class DeviceKeyService : IDeviceKeyService
{
    public const string KeyPrefixMarker = "cnx_";
    private const int MaxActiveKeysPerUser = 10;
    private const int MaxNameLength = 100;

    private readonly DbConexy _db;
    private readonly IUserRepository _users;
    private readonly ITokenService _tokens;
    private readonly TimeProvider _time;

    public DeviceKeyService(DbConexy db, IUserRepository users, ITokenService tokens, TimeProvider time)
    {
        _db = db;
        _users = users;
        _tokens = tokens;
        _time = time;
    }

    public async Task<IssuedDeviceKey> IssueAsync(Guid userId, string? name, CancellationToken ct)
    {
        var active = await _db.DeviceApiKeys.CountAsync(k => k.UserId == userId && k.RevokedAt == null, ct);
        if (active >= MaxActiveKeysPerUser)
            throw new InvalidOperationException($"Достигнут лимит активных ключей ({MaxActiveKeysPerUser}). Отзовите ненужные.");

        var key = KeyPrefixMarker + Base64Url(RandomNumberGenerator.GetBytes(32));
        var cleanName = string.IsNullOrWhiteSpace(name) ? "Desktop" : name.Trim();
        if (cleanName.Length > MaxNameLength)
            cleanName = cleanName[..MaxNameLength];

        var entity = new DeviceApiKeyEntity
        {
            UserId = userId,
            Name = cleanName,
            KeyHash = Hash(key),
            KeyPrefix = key[..8],
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        };
        _db.DeviceApiKeys.Add(entity);
        await _db.SaveChangesAsync(ct);

        return new IssuedDeviceKey(ToInfo(entity), key);
    }

    public async Task<IReadOnlyList<DeviceKeyInfo>> ListAsync(Guid userId, CancellationToken ct)
    {
        var rows = await _db.DeviceApiKeys.AsNoTracking()
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(ToInfo).ToList();
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid keyId, CancellationToken ct)
    {
        var entity = await _db.DeviceApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId, ct);
        if (entity is null)
            return false;

        entity.RevokedAt ??= _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<TokenResponse?> ExchangeAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || !key.StartsWith(KeyPrefixMarker, StringComparison.Ordinal))
            return null;

        var hash = Hash(key.Trim());
        var entity = await _db.DeviceApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
        if (entity is null || entity.RevokedAt is not null)
            return null;

        var user = await _users.GetByIdAsync(entity.UserId, ct);
        if (user is null)
            return null;

        entity.LastUsedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);

        return _tokens.CreateToken(user);
    }

    internal static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static DeviceKeyInfo ToInfo(DeviceApiKeyEntity e) =>
        new(e.Id, e.Name, e.KeyPrefix, e.CreatedAt, e.LastUsedAt, e.RevokedAt is not null);
}
