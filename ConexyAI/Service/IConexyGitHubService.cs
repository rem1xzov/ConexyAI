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
        CancellationToken ct = default);

    /// <summary>
    /// Returns the PAT resolved from configuration or the environment, or null when absent.
    /// This lets the agent run GitHub workflows without the caller forwarding a token.
    /// </summary>
    string? GetConfiguredToken();
}
