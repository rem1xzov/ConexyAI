using ConexyAI.Entity;
using ConexyAI.Service.Auth;

namespace ConexyAI.Repository;

// GITHUB_OAUTH: добавлено 2026-09-19
public interface IUserRepository
{
    Task<User?> GetByGitHubIdAsync(string gitHubId, CancellationToken ct = default);

    // EMAIL_AUTH: добавлено 2026-09-19
    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task AddAsync(User user, CancellationToken ct = default);
    Task UpdateAsync(User user, CancellationToken ct = default);

    // ADMIN_PANEL: добавлено 2026-09-19
    Task<(IReadOnlyList<User> Items, int TotalCount)> GetUsersPaginatedAsync(int page, int pageSize, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Inserts a bare placeholder user for <paramref name="userId"/> if none exists. Used by
    /// the development dev-token endpoint so the new FK constraints remain satisfiable.
    /// </summary>
    Task EnsureExistsAsync(Guid userId, CancellationToken ct = default);

    // TOKEN_REVOCATION: добавлено 2026-09-24 (ревью M19)
    /// <summary>Loads just the fields the per-request token check needs; <c>null</c> when the user is gone.</summary>
    Task<UserAuthState?> GetAuthStateAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Atomically increments <see cref="User.TokenVersion"/>, revoking every JWT issued to the user
    /// so far. Returns <c>false</c> when the user does not exist.
    /// </summary>
    Task<bool> BumpTokenVersionAsync(Guid userId, CancellationToken ct = default);
}
