using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Repository;

/// <summary>
/// EMAIL_VERIFICATION: persistence for pending sign-ups and password resets. One address has at most
/// one live code per flow, so every write replaces what was there before.
/// </summary>
public interface IEmailVerificationRepository
{
    /// <summary>
    /// Newest code for an address, or <c>null</c> when nothing is pending for it. Pass <paramref name="purpose"/> to
    /// look inside one flow, or <c>null</c> for the newest row of any flow (the resend path does that, since the
    /// pending row itself says which flow needs the mail).
    /// </summary>
    Task<EmailVerificationCodeEntity?> GetLatestAsync(
        string email, string? purpose = null, CancellationToken ct = default);

    /// <summary>Newest code requested from one IP — the other half of the resend cooldown.</summary>
    Task<EmailVerificationCodeEntity?> GetLatestForIpAsync(string ip, CancellationToken ct = default);

    /// <summary>Drops every code of the address in this row's flow and stores this one instead.</summary>
    Task ReplaceAsync(EmailVerificationCodeEntity row, CancellationToken ct = default);

    /// <summary>Persists a changed row (a spent attempt).</summary>
    Task UpdateAsync(EmailVerificationCodeEntity row, CancellationToken ct = default);

    /// <summary>Removes every code of the address in one flow (used after a successful confirmation).</summary>
    Task RemoveAllForEmailAsync(string email, string purpose, CancellationToken ct = default);
}

public class EmailVerificationRepository : IEmailVerificationRepository
{
    private readonly DbContext.DbConexy _context;

    public EmailVerificationRepository(DbContext.DbConexy context)
    {
        _context = context;
    }

    public async Task<EmailVerificationCodeEntity?> GetLatestAsync(
        string email, string? purpose = null, CancellationToken ct = default)
    {
        // Tracked (no AsNoTracking): the caller updates the row it gets back (a spent attempt, a
        // refreshed expiry), and a second detached copy of the same key would collide with the one
        // the context already holds.
        return await _context.EmailVerificationCodes
            .Where(c => c.Email == email && (purpose == null || c.Purpose == purpose))
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<EmailVerificationCodeEntity?> GetLatestForIpAsync(string ip, CancellationToken ct = default)
    {
        return await _context.EmailVerificationCodes
            .Where(c => c.LastSentIp == ip)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task ReplaceAsync(EmailVerificationCodeEntity row, CancellationToken ct = default)
    {
        // Load-and-remove instead of ExecuteDeleteAsync: the in-memory provider used by the test
        // suite does not support ExecuteDelete, and one address owns a handful of rows at most.
        // Scoped to the row's own flow: a pending reset must survive starting a sign-up for the same
        // address (and vice versa), otherwise one form would silently cancel the other.
        var stale = await _context.EmailVerificationCodes
            .Where(c => c.Email == row.Email && c.Purpose == row.Purpose)
            .ToListAsync(ct);

        if (stale.Count > 0)
        {
            _context.EmailVerificationCodes.RemoveRange(stale);
        }

        _context.EmailVerificationCodes.Add(row);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(EmailVerificationCodeEntity row, CancellationToken ct = default)
    {
        _context.EmailVerificationCodes.Update(row);
        await _context.SaveChangesAsync(ct);
    }

    public async Task RemoveAllForEmailAsync(string email, string purpose, CancellationToken ct = default)
    {
        var rows = await _context.EmailVerificationCodes
            .Where(c => c.Email == email && c.Purpose == purpose)
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return;
        }

        _context.EmailVerificationCodes.RemoveRange(rows);
        await _context.SaveChangesAsync(ct);
    }
}
