using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Repository;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
// MEMORY_CONTROL: переписано 2026-09-24 — ревью H5. Раньше список фактов заменялся целиком (новые id
// на каждом прогоне), поэтому удалить один факт было невозможно: следующий прогон извлекал его снова
// из того же диалога. Теперь замена — это diff, а удалённые пользователем факты остаются надгробиями.
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
            .Where(f => f.UserId == userId && !f.IsSuppressed)
            .OrderBy(f => f.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetSuppressedTextsAsync(Guid userId, CancellationToken ct = default)
    {
        return await _context.UserMemoryFacts
            .AsNoTracking()
            .Where(f => f.UserId == userId && f.IsSuppressed)
            .OrderBy(f => f.UpdatedAt)
            .Select(f => f.FactText)
            .ToListAsync(ct);
    }

    public async Task ReplaceFactsAsync(Guid userId, IReadOnlyList<string> facts, Guid? sourceChatId = null, CancellationToken ct = default)
    {
        var existing = await _context.UserMemoryFacts
            .Where(f => f.UserId == userId)
            .ToListAsync(ct);

        var suppressed = existing.Where(f => f.IsSuppressed).Select(f => Normalize(f.FactText)).ToHashSet(StringComparer.Ordinal);
        var active = existing.Where(f => !f.IsSuppressed).ToList();
        var wanted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (string.IsNullOrWhiteSpace(fact)) continue;
            var key = Normalize(fact);
            if (suppressed.Contains(key) || !seen.Add(key)) continue;
            wanted.Add(fact.Trim());
        }

        var now = DateTime.UtcNow;
        var keep = new HashSet<string>(wanted.Select(Normalize), StringComparer.Ordinal);
        _context.UserMemoryFacts.RemoveRange(active.Where(f => !keep.Contains(Normalize(f.FactText))));

        var present = active.Select(f => Normalize(f.FactText)).ToHashSet(StringComparer.Ordinal);
        foreach (var fact in wanted)
        {
            if (present.Contains(Normalize(fact))) continue;
            _context.UserMemoryFacts.Add(new UserMemoryFactEntity
            {
                UserId = userId,
                FactText = fact,
                SourceChatId = sourceChatId,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task<bool> SuppressFactAsync(Guid userId, Guid factId, CancellationToken ct = default)
    {
        var fact = await _context.UserMemoryFacts
            .FirstOrDefaultAsync(f => f.Id == factId && f.UserId == userId && !f.IsSuppressed, ct);
        if (fact is null)
            return false;

        fact.IsSuppressed = true;
        fact.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> SuppressAllAsync(Guid userId, CancellationToken ct = default)
    {
        var facts = await _context.UserMemoryFacts
            .Where(f => f.UserId == userId && !f.IsSuppressed)
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var fact in facts)
        {
            fact.IsSuppressed = true;
            fact.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(ct);
        return facts.Count;
    }

    /// <summary>Case- and whitespace-insensitive identity of a fact.</summary>
    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .TrimEnd('.', '!', ';')
            .ToLowerInvariant();
}
