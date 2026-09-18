using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ConexyAI.Repository;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
public class UsageRepository : IUsageRepository
{
    private readonly DbConexy _context;

    public UsageRepository(DbConexy context)
    {
        _context = context;
    }

    public async Task<UserUsageCounterEntity?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        return await _context.UserUsageCounters
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId, ct);
    }

    public async Task UpsertAsync(UserUsageCounterEntity entity, CancellationToken ct = default)
    {
        // Prefer mutating the already-tracked instance so we never attach a second
        // instance with the same key (EF Core ChangeTracker conflict).
        var local = _context.UserUsageCounters.Local.FirstOrDefault(e => e.UserId == entity.UserId);
        if (local != null)
        {
            _context.Entry(local).CurrentValues.SetValues(entity);
            await _context.SaveChangesAsync(ct);
            return;
        }

        // INSERT-first, falling back to UPDATE on a unique-violation race. This avoids the
        // TOCTOU gap of "AnyAsync(exists) -> Add/Update", where two concurrent requests for
        // a brand-new user can both see "does not exist" and both INSERT (one then throws).
        try
        {
            await _context.UserUsageCounters.AddAsync(entity, ct);
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Lost the insert race: another request created the row. Detach the failed
            // entity, then update the winner in place.
            _context.Entry(entity).State = EntityState.Detached;
            _context.UserUsageCounters.Update(entity);
            await _context.SaveChangesAsync(ct);
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
