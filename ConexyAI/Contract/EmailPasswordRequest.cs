namespace ConexyAI.Contract;

// EMAIL_AUTH: добавлено 2026-09-19
/// <summary>Credentials for email/password registration and login.</summary>
public record EmailPasswordRequest(string Email, string Password);
