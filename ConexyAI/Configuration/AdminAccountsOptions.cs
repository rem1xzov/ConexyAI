namespace ConexyAI.Configuration;

// EMAIL_AUTH: добавлено 2026-09-19
/// <summary>
/// Admin identifiers parsed from the <c>ADMIN_ACCOUNTS</c> environment variable
/// (comma-separated; each entry is a GitHub username or an email). Matching is
/// case-insensitive. A user is an admin when either their <c>GitHubUsername</c> or
/// <c>Email</c> appears in this set.
/// </summary>
public class AdminAccountsOptions
{
    public const string EnvVar = "ADMIN_ACCOUNTS";

    public HashSet<string> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Matches(string? email, string? gitHubUsername)
    {
        if (email is not null && Accounts.Contains(email)) return true;
        if (gitHubUsername is not null && Accounts.Contains(gitHubUsername)) return true;
        return false;
    }
}
