using ConexyAI.Model;

namespace ConexyAI.Entity;

// GITHUB_OAUTH: добавлено 2026-09-19
/// <summary>
/// A registered user. Password credentials are optional and only populated for future
/// email/password accounts; pure OAuth (GitHub) users have <see cref="GitHubId"/> set and
/// leave <see cref="PasswordHash"/>/<see cref="Email"/> null when GitHub exposes no email.
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string? Email { get; set; }

    public bool EmailConfirmed { get; set; }

    public string? PasswordHash { get; set; }

    /// <summary>GitHub numeric user id (as a string) — the stable OAuth identity key.</summary>
    public string? GitHubId { get; set; }

    public string? GitHubUsername { get; set; }

    public SubscriptionTier SubscriptionTier { get; set; } = SubscriptionTier.Free;

    // EMAIL_AUTH: добавлено 2026-09-19
    /// <summary>Whether the user is an administrator (granted via AdminAccounts on login).</summary>
    public bool IsAdmin { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;
}
