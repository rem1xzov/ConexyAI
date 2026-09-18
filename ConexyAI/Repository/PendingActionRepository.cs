using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Repository;

// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
public class PendingActionRepository : IPendingActionRepository
{
    private readonly DbConexy _context;

    public PendingActionRepository(DbConexy context)
    {
        _context = context;
    }

    public async Task<PendingActionEntity> CreateAsync(PendingActionEntity entity, CancellationToken ct = default)
    {
        await _context.PendingActions.AddAsync(entity, ct);
        await _context.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<PendingActionEntity?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.PendingActions.FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task UpdateAsync(PendingActionEntity entity, CancellationToken ct = default)
    {
        var local = _context.PendingActions.Local.FirstOrDefault(e => e.Id == entity.Id);
        if (local != null)
        {
            _context.Entry(local).CurrentValues.SetValues(entity);
        }
        else
        {
            _context.PendingActions.Update(entity);
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}
