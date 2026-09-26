namespace ConexyAI.Entity;

/// <summary>
/// EMAIL_VERIFICATION: one sign-up (or password reset) waiting for its 6-digit code.
/// <para>
/// The row carries the hash of the password the user chose, and the account is created only once the
/// code is entered. That keeps three properties at once: nothing half-registered lands in
/// <c>users</c>; an address cannot be squatted by someone who cannot read its inbox (they can never
/// finish), because the row is simply replaced by the next attempt; and every row in <c>users</c> is
/// confirmed by definition, which is what lets the login path require confirmation without breaking
/// accounts that already exist.
/// </para>
/// <para>
/// PASSWORD_RESET: the same row serves password resets — see <see cref="Purpose"/>. A reset row has no
/// password hash, because the new password is chosen after the code has been sent, and the code only
/// proves that the address belongs to the account owner.
/// </para>
/// </summary>
public class EmailVerificationCodeEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Lower-case address — the only thing the user has to prove ownership of.</summary>
    public string Email { get; set; } = null!;

    /// <summary>Which flow this code belongs to — see <see cref="EmailCodePurposes"/>.</summary>
    public string Purpose { get; set; } = EmailCodePurposes.SignUp;

    /// <summary>BCrypt hash of the 6-digit code. The code itself is never stored, logged or returned.</summary>
    public string CodeHash { get; set; } = null!;

    /// <summary>
    /// Hash of the chosen password, applied to the account only when the code is confirmed. <c>null</c>
    /// for <see cref="EmailCodePurposes.PasswordReset"/> rows, where the password is hashed when the
    /// reset is confirmed rather than when the code is sent.
    /// </summary>
    public string? PasswordHash { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Wrong guesses left. At zero the code is dead and a new one must be requested.</summary>
    public int AttemptsLeft { get; set; }

    /// <summary>Address the code was requested from — the second half of the resend cooldown.</summary>
    public string? LastSentIp { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
