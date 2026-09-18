using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Repository;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
public class UserMemoryRepository : IUserMemoryRepository
{
    private readonly DbConexy _context;

    public UserMemoryRepository(DbConexy context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<UserMemoryFactEntity>> GetFactsAsync(Guid userId, CancellationToken ct = default)
    {
        return await _context.UserMemoryFacts
            .AsNoTracking()
            .Where(f => f.UserId == userId)
            .OrderBy(f => f.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task ReplaceFactsAsync(Guid userId, IReadOnlyList<string> facts, CancellationToken ct = default)
    {
        var existing = await _context.UserMemoryFacts
            .Where(f => f.UserId == userId)
            .ToListAsync(ct);

        _context.UserMemoryFacts.RemoveRange(existing);

        var now = DateTime.UtcNow;
        foreach (var fact in facts)
        {
            if (string.IsNullOrWhiteSpace(fact))
                continue;

            _context.UserMemoryFacts.Add(new UserMemoryFactEntity
            {
                UserId = userId,
                FactText = fact.Trim(),
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await _context.SaveChangesAsync(ct);
    }
}
