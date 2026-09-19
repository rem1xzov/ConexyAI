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
}
