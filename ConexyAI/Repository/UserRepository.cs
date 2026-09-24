using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Service.Auth;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Repository;

// GITHUB_OAUTH: добавлено 2026-09-19
public class UserRepository : IUserRepository
{
    private readonly DbConexy _context;
    // TOKEN_REVOCATION: добавлено 2026-09-24 — любая запись/удаление пользователя сбрасывает его
    // запись в кэше проверки токена, чтобы логаут/разжалование/удаление действовали сразу.
    private readonly IUserAuthStateCache? _authStateCache;

    public UserRepository(DbConexy context, IUserAuthStateCache? authStateCache = null)
    {
        _context = context;
        _authStateCache = authStateCache;
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
        var entry = local != null ? _context.Entry(local) : _context.Users.Update(user);
        if (local != null)
        {
            entry.CurrentValues.SetValues(user);
        }

        // TOKEN_REVOCATION: добавлено 2026-09-24 — обычное обновление (логин, смена тарифа, make-admin)
        // пишет строку целиком из копии, прочитанной раньше. Если между чтением и записью случился
        // логаут, старая копия вернула бы прежний TokenVersion и «оживила» отозванные токены.
        // Поэтому версия меняется только атомарным BumpTokenVersionAsync.
        entry.Property(u => u.TokenVersion).IsModified = false;

        await _context.SaveChangesAsync(ct);
        _authStateCache?.Invalidate(user.Id);
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
        _authStateCache?.Invalidate(id);
    }

    // TOKEN_REVOCATION: добавлено 2026-09-24 (ревью M19)
    public async Task<UserAuthState?> GetAuthStateAsync(Guid userId, CancellationToken ct = default)
    {
        return await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserAuthState(u.TokenVersion, u.IsAdmin))
            .FirstOrDefaultAsync(ct);
    }

    // TOKEN_REVOCATION: добавлено 2026-09-24 (ревью M19)
    public async Task<bool> BumpTokenVersionAsync(Guid userId, CancellationToken ct = default)
    {
        bool found;
        if (_context.Database.IsRelational())
        {
            // Один UPDATE ... SET "TokenVersion" = "TokenVersion" + 1: без гонки чтение-запись.
            var rows = await _context.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.TokenVersion, u => u.TokenVersion + 1), ct);
            found = rows > 0;
        }
        else
        {
            // Нерелейционный провайдер (InMemory в тестах) не умеет ExecuteUpdate.
            var local = _context.Users.Local.FirstOrDefault(u => u.Id == userId);
            if (local != null)
                await _context.Entry(local).ReloadAsync(ct);

            var user = local ?? await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            found = user is not null && _context.Entry(user).State != EntityState.Detached;
            if (found)
            {
                user!.TokenVersion++;
                await _context.SaveChangesAsync(ct);
            }
        }

        _authStateCache?.Invalidate(userId);
        return found;
    }
}
