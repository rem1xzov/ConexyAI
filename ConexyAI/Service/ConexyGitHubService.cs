using ConexyAI.Configuration;
using ConexyAI.Contract;
using Microsoft.Extensions.Options;
using Octokit;

namespace ConexyAI.Service;

public class ConexyGitHubService : IConexyGitHubService
{
    private readonly IConexyWorkspaceService _workspaceService;
    private readonly string? _configuredToken;

    public ConexyGitHubService(IConexyWorkspaceService workspaceService, IOptions<GitHubOptions> options)
    {
        _workspaceService = workspaceService;
        _configuredToken = ResolveConfiguredToken(options.Value.PersonalAccessToken);
    }

    public string? GetConfiguredToken() => _configuredToken;

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

    private static string? ResolveConfiguredToken(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        return Environment.GetEnvironmentVariable("GITHUB_PAT")
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    }
}
