using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ConexyAI.Repository;

public class ConexyRepository : IConexyRepository
{
    private readonly DbConexy _context;

    public ConexyRepository(DbConexy context)
    {
        _context = context;
    }

    public async Task<ConexyEntity> CreateOrGetAsync(ConexyEntity entity, CancellationToken ct = default)
    {
        try
        {
            await _context.Conexy.AddAsync(entity, ct);
            await _context.SaveChangesAsync(ct);
            return entity;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Lost the creation race: another request already inserted the same key.
            // Detach the failed entity so it isn't re-saved, then return the winner.
            _context.Entry(entity).State = EntityState.Detached;

            var existing = await _context.Conexy
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == entity.Id, ct);

            if (existing is null)
            {
                throw new InvalidOperationException(
                    $"Unique constraint violated for '{entity.Id}' but the entity was not found on retry.");
            }

            return existing;
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public async Task<ConexyEntity?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Conexy.FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<ConexyEntity?> GetByIdAsNoTrackingAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Conexy
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<IEnumerable<ConexyEntity>> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await _context.Conexy
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<int> GetRequestCountInWindowAsync(Guid userId, DateTime since, CancellationToken ct = default)
    {
        return await _context.Conexy
            .CountAsync(x => x.UserId == userId && x.CreatedAt >= since, ct);
    }

    public async Task UpdateAsync(ConexyEntity entity, CancellationToken ct = default)
    {
        // Prefer mutating the already-tracked instance so we never attach a second
        // instance with the same key (EF Core ChangeTracker conflict).
        var local = _context.Conexy.Local.FirstOrDefault(e => e.Id == entity.Id);
        if (local != null)
        {
            _context.Entry(local).CurrentValues.SetValues(entity);
        }
        else
        {
            _context.Conexy.Update(entity);
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}