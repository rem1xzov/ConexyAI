namespace ConexyAI.Entity;

// PASSWORD_RESET: добавлено 2026-09-26
/// <summary>
/// Which flow a row in <c>email_verification_codes</c> belongs to. Both flows share the whole code
/// lifecycle (one live code per address, expiry, attempt budget, resend cooldown), but they must never
/// be interchangeable: a sign-up code turns the stored password hash into a new account, while a reset
/// code only proves the address and lets the user choose a password afterwards.
/// </summary>
public static class EmailCodePurposes
{
    /// <summary>Sign-up: the row carries the password hash for the account that does not exist yet.</summary>
    public const string SignUp = "signup";

    /// <summary>Password reset for an account that already exists.</summary>
    public const string PasswordReset = "reset";
}
