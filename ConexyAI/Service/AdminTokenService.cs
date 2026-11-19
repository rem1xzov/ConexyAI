using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

// ADMIN_TOKEN_CREDIT: добавлено 2026-11-19 — админ начисляет токены пользователю вручную из
// админ-панели (web/mobile). Начисление добавляется к купленному пулу User.C*TokenTopUp — тому же,
// который пополняют оплаты, — и пишется в журнал admin_token_grant.
public interface IAdminTokenService
{
    /// <summary>
    /// Credits tokens to one user's pool atomically and writes the audit row.
    /// </summary>
    /// <returns><c>false</c> when the target user does not exist.</returns>
    Task<bool> CreditAsync(
        Guid targetUserId,
        Guid adminId,
        string? pool,
        long amount,
        string? reason,
        CancellationToken ct = default);
}

public sealed class AdminTokenService : IAdminTokenService
{
    /// <summary>Upper sanity bound for one manual credit (1e12 tokens).</summary>
    public const long MaxAmount = 1_000_000_000_000L;

    /// <summary>Longest accepted reason.</summary>
    public const int MaxReasonChars = 500;

    private readonly DbConexy _db;
    private readonly ILogger<AdminTokenService> _logger;

    public AdminTokenService(DbConexy db, ILogger<AdminTokenService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> CreditAsync(
        Guid targetUserId,
        Guid adminId,
        string? pool,
        long amount,
        string? reason,
        CancellationToken ct = default)
    {
        var normalized = TokenPools.Normalize(pool)
            ?? throw new AuthException("invalid_pool", "Пул должен быть Coder или Cowork.", 400);
        if (amount <= 0)
            throw new AuthException("invalid_amount", "Сумма должна быть положительным целым числом.", 400);
        if (amount > MaxAmount)
            throw new AuthException("invalid_amount", $"Сумма больше допустимой ({MaxAmount:N0}).", 400);

        if (!await _db.Users.AnyAsync(u => u.Id == targetUserId, ct))
            return false;

        var reasonText = TrimReason(reason);
        var relational = _db.Database.IsRelational();
        await using var transaction = relational ? await _db.Database.BeginTransactionAsync(ct) : null;

        // ATOMIC: увеличиваем пул одним UPDATE, иначе параллельные начисления теряли бы одно из них.
        if (relational)
        {
            if (normalized == TokenPools.Cowork)
            {
                await _db.Users.Where(u => u.Id == targetUserId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.CoworkTokenTopUp, u => u.CoworkTokenTopUp + amount), ct);
            }
            else
            {
                await _db.Users.Where(u => u.Id == targetUserId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.CoderTokenTopUp, u => u.CoderTokenTopUp + amount), ct);
            }
        }
        else
        {
            // Тесты работают на EF InMemory: ExecuteUpdate там недоступен, поэтому обновляем сущность.
            var user = await _db.Users.FirstAsync(u => u.Id == targetUserId, ct);
            if (normalized == TokenPools.Cowork)
                user.CoworkTokenTopUp += amount;
            else
                user.CoderTokenTopUp += amount;
        }

        _db.AdminTokenGrants.Add(new AdminTokenGrantEntity
        {
            UserId = targetUserId,
            AdminId = adminId,
            Pool = normalized,
            Amount = amount,
            Reason = reasonText,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "Admin {AdminId} credited {Amount} {Pool} tokens to user {TargetId} (reason: {Reason}).",
            adminId, amount, normalized, targetUserId, reasonText ?? "-");
        return true;
    }

    private static string? TrimReason(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return text.Length <= MaxReasonChars ? text : text[..MaxReasonChars];
    }
}
