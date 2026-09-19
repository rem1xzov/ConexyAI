namespace ConexyAI.Contract;

// EMAIL_AUTH: добавлено 2026-09-19
/// <summary>Public profile of the authenticated user, returned by <c>GET /api/auth/me</c>.</summary>
public record UserProfileDto(
    string Email,
    string DisplayName,
    string Tier,
    bool IsAdmin);
