namespace ConexyAI.Configuration;

/// <summary>
/// Binds the <c>GitHub</c> section of appsettings.json. The PAT may alternatively be
/// supplied through the <c>GITHUB_PAT</c> or <c>GITHUB_TOKEN</c> environment variable.
/// </summary>
public class GitHubOptions
{
    public const string SectionName = "GitHub";

    public string PersonalAccessToken { get; set; } = string.Empty;
}
