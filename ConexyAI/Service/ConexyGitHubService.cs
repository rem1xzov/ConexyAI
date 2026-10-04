using System.Text;
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
            var client = CreateClient(token);
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
        string? repoFolder = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new GitOperationResult(false, null, "GitHub token is required.");

        if (string.IsNullOrWhiteSpace(title))
            return new GitOperationResult(false, null, "Pull request title is required.");

        var resolved = await ResolveRepoAsync(new GitHubApiContext(taskId, token, repo, repoFolder), ct);
        if (resolved.Error is not null)
            return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(token);
            var pr = await client.PullRequest.Create(resolved.Owner!, resolved.Repo!, new NewPullRequest(title, headBranch, baseBranch)
            {
                Body = string.IsNullOrWhiteSpace(body) ? "Automated change by ConexyAI." : body
            });

            return new GitOperationResult(true, $"Pull request #{pr.Number} created.", null, pr.HtmlUrl);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    // ---------------------------------------------------------------- GITHUB_FULL: REST-операции

    public async Task<GitOperationResult> ListBranchesAsync(GitHubApiContext context, CancellationToken ct = default)
    {
        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var branches = await client.Repository.Branch.GetAll(resolved.Owner!, resolved.Repo!);
            if (branches.Count == 0)
                return new GitOperationResult(true, "Веток нет.", null);

            var sb = new StringBuilder($"Ветки {resolved.Owner}/{resolved.Repo} ({branches.Count}):");
            foreach (var b in branches)
                sb.Append($"\n- {b.Name} ({Short(b.Commit?.Sha)})");
            return new GitOperationResult(true, sb.ToString(), null);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> ListCommitsAsync(GitHubApiContext context, string? branch, int limit, CancellationToken ct = default)
    {
        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var request = new CommitRequest();
            if (!string.IsNullOrWhiteSpace(branch)) request.Sha = branch;
            var commits = await client.Repository.Commit.GetAll(resolved.Owner!, resolved.Repo!, request);

            var take = Math.Clamp(limit <= 0 ? 20 : limit, 1, 100);
            var sb = new StringBuilder($"Коммиты {resolved.Owner}/{resolved.Repo}{(string.IsNullOrWhiteSpace(branch) ? string.Empty : " @" + branch)}:");
            foreach (var c in commits.Take(take))
            {
                var firstLine = (c.Commit?.Message ?? string.Empty).Split('\n', 2)[0];
                sb.Append($"\n- {Short(c.Sha)} {c.Commit?.Author?.Date:yyyy-MM-dd} {c.Commit?.Author?.Name}: {firstLine}");
            }
            return new GitOperationResult(true, sb.ToString(), null);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> ListIssuesAsync(GitHubApiContext context, string state, int limit, CancellationToken ct = default)
    {
        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var request = new RepositoryIssueRequest { State = ParseStateFilter(state), Filter = IssueFilter.All };
            var issues = await client.Issue.GetAllForRepository(resolved.Owner!, resolved.Repo!, request);

            var take = Math.Clamp(limit <= 0 ? 20 : limit, 1, 100);
            // PR-ы тоже приходят как issues — отсеиваем: у этого инструмента своя ветка.
            var list = issues.Where(i => i.PullRequest is null).Take(take).ToList();
            if (list.Count == 0)
                return new GitOperationResult(true, $"Открытых issue: нет.", null);

            var sb = new StringBuilder($"Issues {resolved.Owner}/{resolved.Repo} ({state}):");
            foreach (var i in list)
                sb.Append($"\n- #{i.Number} [{i.State.StringValue}] {i.Title} ({i.User?.Login}, {i.CreatedAt:yyyy-MM-dd})");
            return new GitOperationResult(true, sb.ToString(), null);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> CreateIssueAsync(GitHubApiContext context, string title, string? body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new GitOperationResult(false, null, "Issue title is required.");

        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var issue = await client.Issue.Create(resolved.Owner!, resolved.Repo!, new NewIssue(title) { Body = body });
            return new GitOperationResult(true, $"Создана issue #{issue.Number}.", null, issue.HtmlUrl);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> CommentIssueAsync(GitHubApiContext context, int number, string body, CancellationToken ct = default)
    {
        if (number <= 0) return new GitOperationResult(false, null, "Issue number is required.");
        if (string.IsNullOrWhiteSpace(body)) return new GitOperationResult(false, null, "Comment body is required.");

        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var comment = await client.Issue.Comment.Create(resolved.Owner!, resolved.Repo!, number, body);
            return new GitOperationResult(true, $"Комментарий добавлен к #{number}.", null, comment.HtmlUrl);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> CloseIssueAsync(GitHubApiContext context, int number, CancellationToken ct = default)
    {
        if (number <= 0) return new GitOperationResult(false, null, "Issue number is required.");

        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var updated = await client.Issue.Update(resolved.Owner!, resolved.Repo!, number, new IssueUpdate { State = ItemState.Closed });
            return new GitOperationResult(true, $"Issue #{updated.Number} закрыта.", null, updated.HtmlUrl);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> GetIssueAsync(GitHubApiContext context, int number, CancellationToken ct = default)
    {
        if (number <= 0) return new GitOperationResult(false, null, "Issue number is required.");

        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var issue = await client.Issue.Get(resolved.Owner!, resolved.Repo!, number);
            var sb = new StringBuilder();
            sb.AppendLine($"Issue #{issue.Number}: {issue.Title}");
            sb.AppendLine($"Статус: {issue.State.StringValue}; автор: {issue.User?.Login}; открыто: {issue.CreatedAt:yyyy-MM-dd}");
            if (issue.Labels.Count > 0)
                sb.AppendLine("Метки: " + string.Join(", ", issue.Labels.Select(l => l.Name)));
            if (issue.PullRequest is not null)
                sb.AppendLine("(это issue — pull request; смотри get_pull_request)");
            if (!string.IsNullOrWhiteSpace(issue.Body))
                sb.AppendLine().AppendLine("Описание:").AppendLine(Clamp(issue.Body, 4000));
            if (!string.IsNullOrWhiteSpace(issue.HtmlUrl))
                sb.AppendLine().AppendLine(issue.HtmlUrl);
            return new GitOperationResult(true, sb.ToString().TrimEnd(), null, issue.HtmlUrl);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> ListPullRequestsAsync(GitHubApiContext context, string state, int limit, CancellationToken ct = default)
    {
        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var request = new PullRequestRequest { State = ParseStateFilter(state) };
            var prs = await client.PullRequest.GetAllForRepository(resolved.Owner!, resolved.Repo!, request);

            var take = Math.Clamp(limit <= 0 ? 20 : limit, 1, 100);
            var list = prs.Take(take).ToList();
            if (list.Count == 0)
                return new GitOperationResult(true, $"Pull request-ов ({state}): нет.", null);

            var sb = new StringBuilder($"Pull requests {resolved.Owner}/{resolved.Repo} ({state}):");
            foreach (var pr in list)
                sb.Append($"\n- #{pr.Number} [{pr.State.StringValue}] {pr.Title}: {pr.Head?.Ref} -> {pr.Base?.Ref} ({pr.User?.Login})");
            return new GitOperationResult(true, sb.ToString(), null);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> GetPullRequestAsync(GitHubApiContext context, int number, CancellationToken ct = default)
    {
        if (number <= 0) return new GitOperationResult(false, null, "Pull request number is required.");

        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var pr = await client.PullRequest.Get(resolved.Owner!, resolved.Repo!, number);
            var sb = new StringBuilder();
            sb.AppendLine($"PR #{pr.Number}: {pr.Title}");
            sb.AppendLine($"Статус: {pr.State.StringValue}{(pr.Merged ? " (merged)" : string.Empty)}{(pr.Mergeable == false ? " — есть конфликты" : string.Empty)}");
            sb.AppendLine($"Ветки: {pr.Head?.Ref} -> {pr.Base?.Ref}");
            sb.AppendLine($"Автор: {pr.User?.Login}; изменено: {pr.UpdatedAt:yyyy-MM-dd}");
            sb.AppendLine($"Комментариев: {pr.Comments}, изменённых файлов: {pr.ChangedFiles}, +{pr.Additions}/-{pr.Deletions}");
            if (!string.IsNullOrWhiteSpace(pr.Body))
                sb.AppendLine().AppendLine("Описание:").AppendLine(pr.Body.Length > 2000 ? pr.Body[..2000] + "…" : pr.Body);
            if (!string.IsNullOrWhiteSpace(pr.HtmlUrl))
                sb.AppendLine().AppendLine(pr.HtmlUrl);
            return new GitOperationResult(true, sb.ToString().TrimEnd(), null, pr.HtmlUrl);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> GetPullRequestFilesAsync(GitHubApiContext context, int number, int limit, CancellationToken ct = default)
    {
        if (number <= 0) return new GitOperationResult(false, null, "Pull request number is required.");

        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var files = await client.PullRequest.Files(resolved.Owner!, resolved.Repo!, number);
            if (files.Count == 0)
                return new GitOperationResult(true, $"PR #{number}: изменённых файлов нет.", null);

            var take = Math.Clamp(limit <= 0 ? 30 : limit, 1, 100);
            var sb = new StringBuilder($"PR #{number}: изменённые файлы ({files.Count}):");
            // Диффы в ответ модели ограничиваем: иначе большой PR не влезает в контекст.
            var patchBudget = 14000;
            foreach (var f in files.Take(take))
            {
                sb.Append($"\n\n### {f.Status} {f.FileName} (+{f.Additions}/-{f.Deletions})");
                if (!string.IsNullOrWhiteSpace(f.Patch) && patchBudget > 0)
                {
                    var patch = f.Patch.Length > patchBudget ? f.Patch[..patchBudget] + "\n… (дифф обрезан)" : f.Patch;
                    patchBudget -= Math.Min(f.Patch.Length, patchBudget);
                    sb.Append('\n').Append(patch);
                }
            }
            if (files.Count > take)
                sb.Append($"\n\n… ещё {files.Count - take} файл(ов) не показано.");
            return new GitOperationResult(true, sb.ToString(), null);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> GetPullRequestCommentsAsync(GitHubApiContext context, int number, int limit, CancellationToken ct = default)
    {
        if (number <= 0) return new GitOperationResult(false, null, "Pull request number is required.");

        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var reviews = await client.PullRequest.Review.GetAll(resolved.Owner!, resolved.Repo!, number);
            var reviewComments = await client.PullRequest.ReviewComment.GetAll(resolved.Owner!, resolved.Repo!, number);
            var issueComments = await client.Issue.Comment.GetAllForIssue(resolved.Owner!, resolved.Repo!, number);

            var take = Math.Clamp(limit <= 0 ? 40 : limit, 1, 100);
            var sb = new StringBuilder($"PR #{number}: обсуждение (ревью {reviews.Count}, inline {reviewComments.Count}, комментарии {issueComments.Count}):");

            if (reviews.Count > 0)
            {
                sb.AppendLine().AppendLine("Ревью:");
                foreach (var r in reviews.Take(take))
                    sb.Append($"\n- [{r.State.StringValue}] {r.User?.Login} {r.SubmittedAt:yyyy-MM-dd}: {FirstLine(r.Body)}");
            }
            if (reviewComments.Count > 0)
            {
                sb.AppendLine().AppendLine().AppendLine("Inline-комментарии:");
                foreach (var c in reviewComments.Take(take))
                    sb.Append($"\n- {c.Path} ({c.User?.Login}): {FirstLine(c.Body)}");
            }
            if (issueComments.Count > 0)
            {
                sb.AppendLine().AppendLine().AppendLine("Комментарии:");
                foreach (var c in issueComments.Take(take))
                    sb.Append($"\n- {c.User?.Login} {c.CreatedAt:yyyy-MM-dd}: {FirstLine(c.Body)}");
            }
            return new GitOperationResult(true, sb.ToString(), null);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> CommentPullRequestAsync(GitHubApiContext context, int number, string body, CancellationToken ct = default)
    {
        // PR — это issue, поэтому обычный комментарий issue и есть комментарий к PR.
        return await CommentIssueAsync(context, number, body, ct);
    }

    public async Task<GitOperationResult> ReviewPullRequestAsync(GitHubApiContext context, int number, string @event, string? body, CancellationToken ct = default)
    {
        if (number <= 0) return new GitOperationResult(false, null, "Pull request number is required.");

        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        var reviewEvent = @event?.Trim().ToLowerInvariant() switch
        {
            "approve" or "approve_pull_request" => (Event: PullRequestReviewEvent.Approve, Label: "approve"),
            "request_changes" or "requestchanges" => (Event: PullRequestReviewEvent.RequestChanges, Label: "request_changes"),
            "comment" or "" or null => (Event: PullRequestReviewEvent.Comment, Label: "comment"),
            _ => ((PullRequestReviewEvent Event, string Label)?)null,
        };
        if (reviewEvent is null)
            return new GitOperationResult(false, null, "event must be 'approve', 'request_changes' or 'comment'.");

        try
        {
            var client = CreateClient(context.Token);
            var review = await client.PullRequest.Review.Create(resolved.Owner!, resolved.Repo!, number,
                new PullRequestReviewCreate { Event = reviewEvent.Value.Event, Body = body });
            return new GitOperationResult(true, $"Ревью отправлено (PR #{number}, {reviewEvent.Value.Label}).", null, review.HtmlUrl);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> MergePullRequestAsync(GitHubApiContext context, int number, string method, CancellationToken ct = default)
    {
        if (number <= 0) return new GitOperationResult(false, null, "Pull request number is required.");

        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        var mergeMethod = method?.Trim().ToLowerInvariant() switch
        {
            "squash" => (Method: PullRequestMergeMethod.Squash, Label: "squash"),
            "rebase" => (Method: PullRequestMergeMethod.Rebase, Label: "rebase"),
            "merge" or "" or null => (Method: PullRequestMergeMethod.Merge, Label: "merge"),
            _ => ((PullRequestMergeMethod Method, string Label)?)null,
        };
        if (mergeMethod is null)
            return new GitOperationResult(false, null, "method must be 'merge', 'squash' or 'rebase'.");

        try
        {
            var client = CreateClient(context.Token);
            var result = await client.PullRequest.Merge(resolved.Owner!, resolved.Repo!, number,
                new MergePullRequest { MergeMethod = mergeMethod.Value.Method });
            return result.Merged
                ? new GitOperationResult(true, $"PR #{number} влит ({mergeMethod.Value.Label}): {result.Message}", null)
                : new GitOperationResult(false, null, $"GitHub не влил PR #{number}: {result.Message}");
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> ClosePullRequestAsync(GitHubApiContext context, int number, CancellationToken ct = default)
    {
        if (number <= 0) return new GitOperationResult(false, null, "Pull request number is required.");

        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var pr = await client.PullRequest.Update(resolved.Owner!, resolved.Repo!, number, new PullRequestUpdate { State = ItemState.Closed });
            return new GitOperationResult(true, $"PR #{pr.Number} закрыт.", null, pr.HtmlUrl);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> ListWorkflowsAsync(GitHubApiContext context, CancellationToken ct = default)
    {
        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var response = await client.Actions.Workflows.List(resolved.Owner!, resolved.Repo!);
            if (response.Workflows.Count == 0)
                return new GitOperationResult(true, "GitHub Actions workflows в репозитории нет.", null);

            var sb = new StringBuilder($"Workflows {resolved.Owner}/{resolved.Repo}:");
            foreach (var w in response.Workflows)
                sb.Append($"\n- {w.Name} (file: {w.Path}, state: {w.State.StringValue}, id: {w.Id})");
            return new GitOperationResult(true, sb.ToString(), null);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> RunWorkflowAsync(GitHubApiContext context, string workflow, string? @ref, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflow))
            return new GitOperationResult(false, null, "workflow (file name or id) is required.");

        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var reference = string.IsNullOrWhiteSpace(@ref) ? "main" : @ref!.Trim();
            await client.Actions.Workflows.CreateDispatch(resolved.Owner!, resolved.Repo!, workflow, new CreateWorkflowDispatch(reference));
            return new GitOperationResult(true, $"Workflow '{workflow}' запущен на ветке '{reference}'.", null);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    public async Task<GitOperationResult> GetCommitStatusAsync(GitHubApiContext context, string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return new GitOperationResult(false, null, "reference (branch, tag or commit SHA) is required.");

        var resolved = await ResolveRepoAsync(context, ct);
        if (resolved.Error is not null) return new GitOperationResult(false, null, resolved.Error);

        try
        {
            var client = CreateClient(context.Token);
            var combined = await client.Repository.Status.GetCombined(resolved.Owner!, resolved.Repo!, reference);

            var sb = new StringBuilder($"CI для '{reference}': общий статус — {combined.State.StringValue}.");
            if (combined.Statuses.Count > 0)
            {
                sb.Append("\nКоммит-статусы:");
                foreach (var s in combined.Statuses)
                    sb.Append($"\n- [{s.State.StringValue}] {s.Context}: {s.Description}");
            }

            // Check runs (GitHub Actions и прочие Apps) могут быть недоступны — тогда просто пропускаем.
            try
            {
                var checks = await client.Check.Run.GetAllForReference(resolved.Owner!, resolved.Repo!, reference);
                if (checks.CheckRuns.Count > 0)
                {
                    sb.Append($"\nCheck runs ({checks.TotalCount}):");
                    foreach (var c in checks.CheckRuns)
                    {
                        var conclusion = c.Conclusion is { } v ? "/" + v.StringValue : string.Empty;
                        sb.Append($"\n- [{c.Status.StringValue}{conclusion}] {c.Name}");
                    }
                }
            }
            catch (NotFoundException)
            {
                // Репозиторий без checks API — не ошибка.
            }

            return new GitOperationResult(true, sb.ToString(), null);
        }
        catch (Exception ex)
        {
            return new GitOperationResult(false, null, Describe(ex));
        }
    }

    // ---------------------------------------------------------------- helpers

    private static GitHubClient CreateClient(string token) =>
        new(new ProductHeaderValue("ConexyAI-Agent")) { Credentials = new Credentials(token) };

    /// <summary>Первая строка текста, обрезанная для сводки (комментарии/ревью бывают огромными).</summary>
    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 400 ? line[..400] + "…" : line;
    }

    private static string Clamp(string text, int max) => text.Length > max ? text[..max] + "…" : text;

    private async Task<(string? Owner, string? Repo, string? Error)> ResolveRepoAsync(GitHubApiContext context, CancellationToken ct)
    {
        var (owner, repo) = await _workspaceService.ResolveRepositoryAsync(context.TaskId, context.Repo, context.RepoFolder, ct);
        return owner is null || repo is null
            ? (null, null, "Could not determine the target GitHub repository. Pass 'repo' (owner/repo) or clone a repository first.")
            : (owner, repo, null);
    }

    private static ItemStateFilter ParseStateFilter(string state) => state?.Trim().ToLowerInvariant() switch
    {
        "closed" => ItemStateFilter.Closed,
        "all" => ItemStateFilter.All,
        _ => ItemStateFilter.Open,
    };

    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "?" : sha[..Math.Min(7, sha.Length)];

    /// <summary>Понятное модели сообщение вместо сырого исключения Octokit.</summary>
    private static string Describe(Exception ex) => ex is ApiException api
        ? $"GitHub API: {api.StatusCode} — {api.Message}"
        : ex.Message;
}
