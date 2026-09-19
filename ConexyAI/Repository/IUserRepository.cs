using ConexyAI.Entity;

namespace ConexyAI.Repository;

// GITHUB_OAUTH: добавлено 2026-09-19
public interface IUserRepository
{
    Task<User?> GetByGitHubIdAsync(string gitHubId, CancellationToken ct = default);
    Task AddAsync(User user, CancellationToken ct = default);
    Task UpdateAsync(User user, CancellationToken ct = default);

    /// <summary>
    /// Inserts a bare placeholder user for <paramref name="userId"/> if none exists. Used by
    /// the development dev-token endpoint so the new FK constraints remain satisfiable.
    /// </summary>
    Task EnsureExistsAsync(Guid userId, CancellationToken ct = default);
}
