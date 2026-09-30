using ConexyAI.Contract;

namespace ConexyAI.Service;

public interface IConexyGitHubService
{
    /// <summary>Opens a pull request via the GitHub API (Octokit).</summary>
    Task<GitOperationResult> CreatePullRequestAsync(
        Guid taskId,
        string token,
        string title,
        string? body,
        string headBranch,
        string baseBranch,
        string? repo = null,
        string? repoFolder = null,
        CancellationToken ct = default);

    /// <summary>
    /// Возвращает аккаунт GitHub, которому принадлежит токен, или <c>null</c>, если токен пуст
    /// либо GitHub его отклонил. По этому аккаунту агент подписывает коммиты (GITHUB_PAT_PER_USER).
    /// </summary>
    Task<GitHubIdentity?> GetIdentityAsync(string token, CancellationToken ct = default);
}
