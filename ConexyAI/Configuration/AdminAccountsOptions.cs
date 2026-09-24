using ConexyAI.Entity;

namespace ConexyAI.Configuration;

// EMAIL_AUTH: добавлено 2026-09-19
/// <summary>
/// Admin identifiers parsed from the <c>ADMIN_ACCOUNTS</c> environment variable
/// (comma-separated). Matching is case-insensitive. Each entry is one of:
/// <list type="bullet">
///   <item><c>github-id:12345</c> — a GitHub numeric user id (the most robust form: it never changes);</item>
///   <item><c>name@example.com</c> — an email; honoured ONLY when that email is verified;</item>
///   <item>anything else — a GitHub username.</item>
/// </list>
/// </summary>
/// <remarks>
/// ADMIN_VERIFIED_ONLY: добавлено 2026-09-24 (ревью H1) — раньше был один метод
/// <c>Matches(email, username)</c>, и регистрация по email/паролю передавала в него
/// НЕПОДТВЕРЖДЁННЫЙ email: любой аноним регистрировал email владельца и мгновенно становился
/// админом. Теперь email-совпадение засчитывается только для email, подтверждённого доверенным
/// каналом (GitHub отдал его как verified, либо <see cref="User.EmailConfirmed"/>).
/// </remarks>
public class AdminAccountsOptions
{
    public const string EnvVar = "ADMIN_ACCOUNTS";

    /// <summary>Prefix of an entry that names a GitHub numeric user id.</summary>
    public const string GitHubIdPrefix = "github-id:";

    public HashSet<string> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the GitHub identity (numeric id or username) is listed.</summary>
    public bool MatchesGitHub(string? gitHubId, string? gitHubUsername)
    {
        if (!string.IsNullOrWhiteSpace(gitHubId) && Accounts.Contains(GitHubIdPrefix + gitHubId.Trim()))
            return true;

        // A GitHub username can never contain '@', so an email entry can't match here.
        return !string.IsNullOrWhiteSpace(gitHubUsername)
            && !gitHubUsername.Contains('@')
            && Accounts.Contains(gitHubUsername.Trim());
    }

    /// <summary>
    /// True when <paramref name="verifiedEmail"/> is listed. The CALLER guarantees the email was
    /// verified by a trusted channel — never pass an address the user merely typed in.
    /// </summary>
    public bool MatchesVerifiedEmail(string? verifiedEmail) =>
        !string.IsNullOrWhiteSpace(verifiedEmail)
        && verifiedEmail.Contains('@')
        && Accounts.Contains(verifiedEmail.Trim());

    /// <summary>
    /// True when a stored user is a configured superadmin: by GitHub id/username, or by an email
    /// that is marked as confirmed. An unconfirmed email (email/password sign-up) never matches.
    /// </summary>
    public bool IsSuperAdmin(User user) =>
        MatchesGitHub(user.GitHubId, user.GitHubUsername)
        || (user.EmailConfirmed && MatchesVerifiedEmail(user.Email));
}
