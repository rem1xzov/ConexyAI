using System.Net.Http.Headers;
using System.Text.Json;
using ConexyAI.Configuration;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// GITHUB_OAUTH: добавлено 2026-09-19
public interface IGitHubOAuthService
{
    /// <summary>Builds the GitHub authorize URL the login endpoint redirects to.</summary>
    string BuildLoginUrl(string state);

    /// <summary>
    /// Exchanges the OAuth <paramref name="code"/> for a GitHub access token, loads the
    /// user's profile (and private email when needed), then finds or creates the local user.
    /// Returns the user id and whether the user was newly created.
    /// </summary>
    Task<GitHubOAuthResult> HandleCallbackAsync(string code, CancellationToken ct = default);
}

public sealed record GitHubOAuthResult(Guid UserId, bool IsNewUser);

public class GitHubOAuthService : IGitHubOAuthService
{
    private const string UserAgent = "ConexyAI";

    private readonly GitHubOAuthOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUserRepository _userRepository;

    public GitHubOAuthService(
        IOptions<GitHubOAuthOptions> options,
        IHttpClientFactory httpClientFactory,
        IUserRepository userRepository)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _userRepository = userRepository;
    }

    public string BuildLoginUrl(string state)
    {
        return QueryHelpers.AddQueryString(_options.AuthorizeEndpoint, new Dictionary<string, string?>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.CallbackUrl,
            ["scope"] = "read:user,user:email",
            ["state"] = state
        });
    }

    public async Task<GitHubOAuthResult> HandleCallbackAsync(string code, CancellationToken ct = default)
    {
        var accessToken = await ExchangeCodeAsync(code, ct);
        var userInfo = await LoadUserAsync(accessToken, ct);

        var now = DateTime.UtcNow;
        var existing = await _userRepository.GetByGitHubIdAsync(userInfo.GitHubId, ct);
        if (existing is null)
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = userInfo.Email,
                EmailConfirmed = userInfo.EmailConfirmed,
                GitHubId = userInfo.GitHubId,
                GitHubUsername = userInfo.Username,
                SubscriptionTier = SubscriptionTier.Free,
                CreatedAt = now,
                LastLoginAt = now
            };

            await _userRepository.AddAsync(user, ct);
            return new GitHubOAuthResult(user.Id, IsNewUser: true);
        }

        // Refresh the mutable profile bits and bump the last-login timestamp on every login.
        existing.Email = userInfo.Email ?? existing.Email;
        existing.EmailConfirmed = existing.EmailConfirmed || userInfo.EmailConfirmed;
        existing.GitHubUsername = userInfo.Username;
        existing.LastLoginAt = now;
        await _userRepository.UpdateAsync(existing, ct);

        return new GitHubOAuthResult(existing.Id, IsNewUser: false);
    }

    private async Task<string> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = _options.CallbackUrl
        });

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"GitHub token exchange failed ({response.StatusCode}): {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubTokenResponse>(
            cancellationToken: ct);

        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new InvalidOperationException("GitHub token exchange returned no access_token.");
        }

        return payload.AccessToken;
    }

    private async Task<GitHubUserInfo> LoadUserAsync(string accessToken, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();

        var profile = await GetJsonAsync<GitHubProfileResponse>(client, _options.UserEndpoint, accessToken, ct);

        var email = profile.Email;
        var emailConfirmed = !string.IsNullOrWhiteSpace(email);

        // A public profile email is verified; otherwise fall back to /user/emails.
        if (string.IsNullOrWhiteSpace(email))
        {
            var emails = await GetJsonAsync<List<GitHubEmailResponse>>(
                client, _options.EmailsEndpoint, accessToken, ct);

            var chosen = emails?
                .Where(e => !string.IsNullOrWhiteSpace(e.Email))
                .OrderByDescending(e => e.Primary)
                .ThenByDescending(e => e.Verified)
                .FirstOrDefault();

            if (chosen is not null)
            {
                email = chosen.Email;
                emailConfirmed = chosen.Verified;
            }
        }

        return new GitHubUserInfo(
            GitHubId: profile.Id.ToString(),
            Username: profile.Login,
            Email: string.IsNullOrWhiteSpace(email) ? null : email,
            EmailConfirmed: emailConfirmed);
    }

    private static async Task<T> GetJsonAsync<T>(
        HttpClient client, string url, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"GitHub API request to {url} failed ({response.StatusCode}): {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        if (result is null)
        {
            throw new InvalidOperationException($"GitHub API request to {url} returned an empty body.");
        }

        return result;
    }

    private sealed record GitHubUserInfo(
        string GitHubId,
        string Username,
        string? Email,
        bool EmailConfirmed);

    private sealed class GitHubTokenResponse
    {
        public string? AccessToken { get; set; }
        public string? TokenType { get; set; }
        public string? Scope { get; set; }
    }

    private sealed class GitHubProfileResponse
    {
        public long Id { get; set; }
        public string Login { get; set; } = string.Empty;
        public string? Email { get; set; }
    }

    private sealed class GitHubEmailResponse
    {
        public string? Email { get; set; }
        public bool Primary { get; set; }
        public bool Verified { get; set; }
    }
}
