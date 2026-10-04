using ConexyAI.Contract;

namespace ConexyAI.Service;

// GITHUB_FULL: добавлено 2026-10-02 — контекст для REST-операций: чей это чат (для резолва репозитория
// из git remote), токен пользователя и, если известно, репозиторий и папка клона в воркспейсе.
public sealed record GitHubApiContext(Guid TaskId, string Token, string? Repo, string? RepoFolder = null);

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

    // GITHUB_FULL: REST-операции, которые не требуют локального клона.
    Task<GitOperationResult> ListBranchesAsync(GitHubApiContext context, CancellationToken ct = default);
    Task<GitOperationResult> ListCommitsAsync(GitHubApiContext context, string? branch, int limit, CancellationToken ct = default);

    Task<GitOperationResult> ListIssuesAsync(GitHubApiContext context, string state, int limit, CancellationToken ct = default);
    Task<GitOperationResult> CreateIssueAsync(GitHubApiContext context, string title, string? body, CancellationToken ct = default);
    Task<GitOperationResult> CommentIssueAsync(GitHubApiContext context, int number, string body, CancellationToken ct = default);
    Task<GitOperationResult> CloseIssueAsync(GitHubApiContext context, int number, CancellationToken ct = default);

    Task<GitOperationResult> ListPullRequestsAsync(GitHubApiContext context, string state, int limit, CancellationToken ct = default);
    Task<GitOperationResult> GetPullRequestAsync(GitHubApiContext context, int number, CancellationToken ct = default);
    /// <summary>Changed files of a PR with their patch (the diff), so the agent can review the code.</summary>
    Task<GitOperationResult> GetPullRequestFilesAsync(GitHubApiContext context, int number, int limit, CancellationToken ct = default);
    /// <summary>Conversation around a PR: reviews, inline review comments and issue comments.</summary>
    Task<GitOperationResult> GetPullRequestCommentsAsync(GitHubApiContext context, int number, int limit, CancellationToken ct = default);
    Task<GitOperationResult> CommentPullRequestAsync(GitHubApiContext context, int number, string body, CancellationToken ct = default);
    Task<GitOperationResult> ReviewPullRequestAsync(GitHubApiContext context, int number, string @event, string? body, CancellationToken ct = default);
    Task<GitOperationResult> MergePullRequestAsync(GitHubApiContext context, int number, string method, CancellationToken ct = default);
    Task<GitOperationResult> ClosePullRequestAsync(GitHubApiContext context, int number, CancellationToken ct = default);

    Task<GitOperationResult> ListWorkflowsAsync(GitHubApiContext context, CancellationToken ct = default);
    Task<GitOperationResult> RunWorkflowAsync(GitHubApiContext context, string workflow, string? @ref, CancellationToken ct = default);

    /// <summary>Combined commit status and check runs for a ref — whether CI passed.</summary>
    Task<GitOperationResult> GetCommitStatusAsync(GitHubApiContext context, string reference, CancellationToken ct = default);

    /// <summary>Reads a single issue with its body and labels.</summary>
    Task<GitOperationResult> GetIssueAsync(GitHubApiContext context, int number, CancellationToken ct = default);
}
