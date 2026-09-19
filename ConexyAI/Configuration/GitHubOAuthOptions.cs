namespace ConexyAI.Configuration;

// GITHUB_OAUTH: добавлено 2026-09-19
/// <summary>
/// Binds GitHub OAuth settings. Client credentials are supplied through the
/// <c>GITHUB_OAUTH_CLIENT_ID</c> / <c>GITHUB_OAUTH_CLIENT_SECRET</c> environment variables
/// (see Program.cs); the callback URL defaults to the production endpoint and can be
/// overridden via the <c>GitHubOAuth:CallbackUrl</c> config key for local testing.
/// </summary>
public class GitHubOAuthOptions
{
    public const string SectionName = "GitHubOAuth";
    public const string ClientIdEnvVar = "GITHUB_OAUTH_CLIENT_ID";
    public const string ClientSecretEnvVar = "GITHUB_OAUTH_CLIENT_SECRET";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Must exactly match the redirect URI configured on the GitHub OAuth App.</summary>
    public string CallbackUrl { get; set; } = "https://conexyai.ru/api/auth/github/callback";

    public string AuthorizeEndpoint { get; set; } = "https://github.com/login/oauth/authorize";
    public string TokenEndpoint { get; set; } = "https://github.com/login/oauth/access_token";
    public string UserEndpoint { get; set; } = "https://api.github.com/user";
    public string EmailsEndpoint { get; set; } = "https://api.github.com/user/emails";
}
