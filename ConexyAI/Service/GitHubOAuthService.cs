using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConexyAI.Configuration;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
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

// TOKEN_REVOCATION: 2026-09-24 — TokenVersion нужен, чтобы выпустить JWT с актуальным claim tv.
public sealed record GitHubOAuthResult(Guid UserId, bool IsNewUser, bool IsAdmin, int TokenVersion);

public class GitHubOAuthService : IGitHubOAuthService
{
    private const string UserAgent = "ConexyAI";

    private readonly GitHubOAuthOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<GitHubOAuthService> _logger;
    // EMAIL_AUTH: добавлено 2026-09-19
    private readonly IOptions<AdminAccountsOptions> _adminOptions;

    public GitHubOAuthService(
        IOptions<GitHubOAuthOptions> options,
        IHttpClientFactory httpClientFactory,
        IUserRepository userRepository,
        ILogger<GitHubOAuthService> logger,
        IOptions<AdminAccountsOptions> adminOptions)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _userRepository = userRepository;
        _logger = logger;
        _adminOptions = adminOptions;
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
        // GITHUB_OAUTH: добавлено 2026-09-19 — debug logging (code prefix only, never the full code).
        _logger.LogInformation("GitHub OAuth callback: code prefix '{CodePrefix}'. Exchanging code...", CodePrefix(code));

        var accessToken = await ExchangeCodeAsync(code, ct);
        _logger.LogInformation("GitHub OAuth callback: code exchanged successfully (access token length {TokenLength}).", accessToken.Length);

        var userInfo = await LoadUserAsync(accessToken, ct);
        _logger.LogInformation(
            "GitHub OAuth callback: loaded GitHub user id={GitHubId}, login={Login}, hasEmail={HasEmail}.",
            userInfo.GitHubId, userInfo.Username, userInfo.Email is not null);

        var now = DateTime.UtcNow;
        // ADMIN_VERIFIED_ONLY: добавлено 2026-09-24 (ревью H1) — админ по ADMIN_ACCOUNTS только через
        // доверенный канал: GitHub id/username или email, который САМ GitHub отдал как verified.
        // Проверяется по живому ответу GitHub, а не по сохранённой строке: владелец получает админа,
        // даже если его email в нашей таблице уже занят чужим (неподтверждённым) аккаунтом.
        var isSuperAdmin = _adminOptions.Value.MatchesGitHub(userInfo.GitHubId, userInfo.Username)
            || userInfo.VerifiedEmails.Any(_adminOptions.Value.MatchesVerifiedEmail);

        var existing = await _userRepository.GetByGitHubIdAsync(userInfo.GitHubId, ct);
        if (existing is null)
        {
            // Email-колонка уникальна. Если этот email уже занят другим аккаунтом (например, кто-то
            // зарегистрировал email владельца через email/пароль), НЕ привязываемся к тому аккаунту
            // (его пароль знает регистрант) и не падаем на уникальном индексе — создаём GitHub-аккаунт
            // без email. Раньше здесь вылетал unique violation, и владелец вообще не мог войти.
            var email = await EmailIfFreeAsync(userInfo.Email, ownerId: null, ct);
            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                EmailConfirmed = email is not null,
                GitHubId = userInfo.GitHubId,
                GitHubUsername = userInfo.Username,
                SubscriptionTier = isSuperAdmin ? SubscriptionTier.Admin : SubscriptionTier.Free,
                // EMAIL_AUTH: добавлено 2026-09-19
                IsAdmin = isSuperAdmin,
                CreatedAt = now,
                LastLoginAt = now
            };

            await _userRepository.AddAsync(user, ct);
            _logger.LogInformation(
                "GitHub OAuth callback: created new user {UserId} (email stored: {EmailStored}).",
                user.Id, email is not null);
            return new GitHubOAuthResult(user.Id, IsNewUser: true, IsAdmin: user.IsAdmin, user.TokenVersion);
        }

        // Refresh the mutable profile bits and bump the last-login timestamp on every login.
        // ADMIN_VERIFIED_ONLY 2026-09-24: email обновляется только на verified-адрес GitHub и только
        // если он не занят другим аккаунтом (иначе логин падал бы на уникальном индексе).
        var freshEmail = await EmailIfFreeAsync(userInfo.Email, existing.Id, ct);
        if (freshEmail is not null)
        {
            existing.Email = freshEmail;
            existing.EmailConfirmed = true;
        }
        existing.GitHubUsername = userInfo.Username;
        // ADMIN_UNLIMITED: добавлено 2026-09-19 — promote ADMIN_ACCOUNTS superadmins on every
        // login, but never demote a make-admin'd user (their IsAdmin lives in the DB).
        if (isSuperAdmin)
        {
            existing.IsAdmin = true;
            existing.SubscriptionTier = SubscriptionTier.Admin;
        }
        existing.LastLoginAt = now;
        await _userRepository.UpdateAsync(existing, ct);
        _logger.LogInformation("GitHub OAuth callback: updated existing user {UserId} last login.", existing.Id);

        return new GitHubOAuthResult(existing.Id, IsNewUser: false, IsAdmin: existing.IsAdmin, existing.TokenVersion);
    }

    /// <summary>
    /// Returns <paramref name="email"/> when no OTHER user holds it (so it can be stored without
    /// violating the unique index), otherwise <c>null</c>.
    /// </summary>
    private async Task<string?> EmailIfFreeAsync(string? email, Guid? ownerId, CancellationToken ct)
    {
        if (email is null)
            return null;

        var holder = await _userRepository.GetByEmailAsync(email, ct);
        if (holder is null || holder.Id == ownerId)
            return email;

        _logger.LogWarning(
            "GitHub OAuth callback: verified GitHub email is already used by another account {HolderId}; not storing it on this GitHub user.",
            holder.Id);
        return null;
    }

    private async Task<string> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();

        // GITHUB_OAUTH: добавлено 2026-09-19 — log the outgoing client_id and redirect_uri
        // (never the client_secret) so a redirect_uri mismatch can be spotted against the
        // value registered on the GitHub OAuth App.
        _logger.LogInformation(
            "GitHub token exchange: client_id='{ClientId}', redirect_uri='{RedirectUri}', code prefix '{CodePrefix}'.",
            _options.ClientId, _options.CallbackUrl, CodePrefix(code));

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
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "GitHub token exchange failed with HTTP {StatusCode}: {Body}",
                response.StatusCode, Truncate(body));
            throw new InvalidOperationException(
                $"GitHub token exchange failed ({response.StatusCode}): {Truncate(body)}");
        }

        // GITHUB_OAUTH: добавлено 2026-09-19 — GitHub returns HTTP 200 even for OAuth errors
        // (incorrect_client_credentials, redirect_uri_mismatch, bad_verification_code, ...)
        // with a JSON body {"error": "...", "error_description": "..."}. Parse the raw body
        // so the error is surfaced instead of being swallowed by the access_token null-check.
        GitHubTokenResponse? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<GitHubTokenResponse>(body);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "GitHub token exchange returned non-JSON body: {Body}", Truncate(body));
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            _logger.LogError(
                "GitHub token exchange returned no access_token. Response body: {Body}",
                Truncate(body));

            var error = payload?.Error;
            var errorDescription = payload?.ErrorDescription;
            var detail = !string.IsNullOrWhiteSpace(error)
                ? (string.IsNullOrWhiteSpace(errorDescription) ? error : $"{error}: {errorDescription}")
                : Truncate(body);
            throw new InvalidOperationException(
                $"GitHub token exchange returned no access_token ({detail}).");
        }

        return payload.AccessToken;
    }

    private async Task<GitHubUserInfo> LoadUserAsync(string accessToken, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();

        var profile = await GetJsonAsync<GitHubProfileResponse>(client, _options.UserEndpoint, accessToken, ct);

        // ADMIN_VERIFIED_ONLY: добавлено 2026-09-24 (ревью H1) — всегда спрашиваем /user/emails (scope
        // user:email): только там GitHub говорит, какие адреса ПОДТВЕРЖДЕНЫ. Публичное поле `email`
        // профиля само по себе ничего о проверке не сообщает, неподтверждённые адреса не храним вовсе.
        List<GitHubEmailResponse>? emails = null;
        try
        {
            emails = await GetJsonAsync<List<GitHubEmailResponse>>(client, _options.EmailsEndpoint, accessToken, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "GitHub OAuth: could not load /user/emails; continuing without a verified email.");
        }

        var verified = (emails ?? new List<GitHubEmailResponse>())
            .Where(e => e.Verified && !string.IsNullOrWhiteSpace(e.Email))
            .Select(e => new { Email = NormalizeEmail(e.Email!), e.Primary })
            .ToList();

        // Stored email: the verified primary one; else the verified public profile email; else any verified.
        var profileEmail = string.IsNullOrWhiteSpace(profile.Email) ? null : NormalizeEmail(profile.Email);
        var email = verified.FirstOrDefault(e => e.Primary)?.Email
            ?? verified.FirstOrDefault(e => e.Email == profileEmail)?.Email
            ?? verified.FirstOrDefault()?.Email;

        return new GitHubUserInfo(
            GitHubId: profile.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Username: profile.Login,
            Email: email,
            VerifiedEmails: verified.Select(e => e.Email).Distinct().ToList());
    }

    // Emails are stored lower-cased (as EmailAuthService does), so uniqueness checks line up.
    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

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
            throw new InvalidOperationException($"GitHub API request to {url} failed ({response.StatusCode}): {Truncate(body)}");
        }

        var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        if (result is null)
        {
            throw new InvalidOperationException($"GitHub API request to {url} returned an empty body.");
        }

        return result;
    }

    /// <param name="Email">The verified email to store (primary first), or <c>null</c>.</param>
    /// <param name="VerifiedEmails">Every address GitHub reports as verified for this account.</param>
    private sealed record GitHubUserInfo(
        string GitHubId,
        string Username,
        string? Email,
        IReadOnlyList<string> VerifiedEmails);

    private sealed class GitHubTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }

    private sealed class GitHubProfileResponse
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("login")]
        public string Login { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string? Email { get; set; }
    }

    private sealed class GitHubEmailResponse
    {
        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("primary")]
        public bool Primary { get; set; }

        [JsonPropertyName("verified")]
        public bool Verified { get; set; }
    }

    // PRIVACY_LOGS: добавлено 2026-09-24 (ревью H3) — тела ответов GitHub в логах/исключениях обрезаются.
    private static string Truncate(string? body) =>
        string.IsNullOrEmpty(body) || body.Length <= 300 ? body ?? string.Empty : body[..300] + "…";

    /// <summary>First 8 characters of the code (or a marker), for debug logging only.</summary>
    private static string CodePrefix(string? code) =>
        string.IsNullOrWhiteSpace(code) ? "<empty>" : code[..Math.Min(8, code.Length)];
}
