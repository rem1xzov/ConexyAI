using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Repository;

// GITHUB_OAUTH: добавлено 2026-09-19
public class UserRepository : IUserRepository
{
    private readonly DbConexy _context;

    public UserRepository(DbConexy context)
    {
        _context = context;
    }

    public async Task<User?> GetByGitHubIdAsync(string gitHubId, CancellationToken ct = default)
    {
        return await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.GitHubId == gitHubId, ct);
    }

    // EMAIL_AUTH: добавлено 2026-09-19
    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        return await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == email, ct);
    }

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    public async Task AddAsync(User user, CancellationToken ct = default)
    {
        await _context.Users.AddAsync(user, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(User user, CancellationToken ct = default)
    {
        var local = _context.Users.Local.FirstOrDefault(e => e.Id == user.Id);
        if (local != null)
        {
            _context.Entry(local).CurrentValues.SetValues(user);
        }
        else
        {
            _context.Users.Update(user);
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task EnsureExistsAsync(Guid userId, CancellationToken ct = default)
    {
        if (await _context.Users.AnyAsync(u => u.Id == userId, ct))
            return;

        _context.Users.Add(new User { Id = userId });
        await _context.SaveChangesAsync(ct);
    }

    // ADMIN_PANEL: добавлено 2026-09-19
    public async Task<(IReadOnlyList<User> Items, int TotalCount)> GetUsersPaginatedAsync(
        int page, int pageSize, CancellationToken ct = default)
    {
        var query = _context.Users.AsNoTracking();
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
            return;

        _context.Users.Remove(user);
        await _context.SaveChangesAsync(ct);
    }
}
