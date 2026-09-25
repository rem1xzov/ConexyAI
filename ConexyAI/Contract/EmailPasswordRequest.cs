namespace ConexyAI.Contract;

// EMAIL_AUTH: добавлено 2026-09-19
/// <summary>Credentials for email/password registration and login.</summary>
public record EmailPasswordRequest(string Email, string Password);

// EMAIL_VERIFICATION: добавлено 2026-09-24
/// <summary>
/// The 6-digit code the user was mailed. The password is NOT sent again: the pending sign-up already
/// holds its hash (see <c>EmailVerificationCodeEntity</c>), so a leaked code alone cannot change the
/// password of anything that already exists.
/// </summary>
public record EmailVerificationRequest(string Email, string Code);

/// <summary>Request of a fresh code for a pending sign-up.</summary>
public record EmailOnlyRequest(string Email);
