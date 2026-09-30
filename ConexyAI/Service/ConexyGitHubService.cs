using ConexyAI.Contract;
using Octokit;

namespace ConexyAI.Service;

public class ConexyGitHubService : IConexyGitHubService
{
    private readonly IConexyWorkspaceService _workspaceService;

    public ConexyGitHubService(IConexyWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    // GITHUB_PAT_PER_USER: личный токен пользователя определяет, от чьего имени агент коммитит.
    public async Task<GitHubIdentity?> GetIdentityAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            var client = new GitHubClient(new ProductHeaderValue("ConexyAI-Agent"))
            {
                Credentials = new Credentials(token)
            };

            var user = await client.User.Current();
            return new GitHubIdentity(user.Login, user.Id);
        }
        catch
        {
            // Невалидный/просроченный токен — идентичность неизвестна, коммит останется
            // подписан именем по умолчанию, но операция всё равно упрётся в авторизацию на push.
            return null;
        }
    }

    public async Task<GitOperationResult> CreatePullRequestAsync(
        Guid taskId,
        string token,
        string title,
        string? body,
        string headBranch,
        string baseBranch,
        string? repo = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new GitOperationResult(false, null, "GitHub token is required.");

        if (string.IsNullOrWhiteSpace(title))
            return new GitOperationResult(false, null, "Pull request title is required.");

        var (owner, repoName) = await _workspaceService.ResolveRepositoryAsync(taskId, repo, ct);
        if (owner == null || repoName == null)
            return new GitOperationResult(false, null, "Could not determine the target GitHub repository.");

        try
        {
            var client = new GitHubClient(new ProductHeaderValue("ConexyAI-Agent"))
            {
                Credentials = new Credentials(token)
            };

            var pr = await client.PullRequest.Create(owner, repoName, new NewPullRequest(title, headBranch, baseBranch)
            {
                Body = string.IsNullOrWhiteSpace(body) ? "Automated change by ConexyAI." : body
            });

            return new GitOperationResult(true, $"Pull request #{pr.Number} created.", null, pr.HtmlUrl);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, ex.Message);
        }
    }
}
