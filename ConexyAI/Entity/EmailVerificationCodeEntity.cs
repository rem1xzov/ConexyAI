namespace ConexyAI.Entity;

/// <summary>
/// EMAIL_VERIFICATION: one sign-up waiting for its 6-digit code.
/// <para>
/// The row carries the hash of the password the user chose, and the account is created only once the
/// code is entered. That keeps three properties at once: nothing half-registered lands in
/// <c>users</c>; an address cannot be squatted by someone who cannot read its inbox (they can never
/// finish), because the row is simply replaced by the next attempt; and every row in <c>users</c> is
/// confirmed by definition, which is what lets the login path require confirmation without breaking
/// accounts that already exist.
/// </para>
/// </summary>
public class EmailVerificationCodeEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Lower-case address — the only thing the user has to prove ownership of.</summary>
    public string Email { get; set; } = null!;

    /// <summary>BCrypt hash of the 6-digit code. The code itself is never stored, logged or returned.</summary>
    public string CodeHash { get; set; } = null!;

    /// <summary>Hash of the chosen password, applied to the account only when the code is confirmed.</summary>
    public string PasswordHash { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    /// <summary>Wrong guesses left. At zero the code is dead and a new one must be requested.</summary>
    public int AttemptsLeft { get; set; }

    /// <summary>Address the code was requested from — the second half of the resend cooldown.</summary>
    public string? LastSentIp { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
