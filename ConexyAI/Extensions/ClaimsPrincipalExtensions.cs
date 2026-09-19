using System.Security.Claims;

namespace ConexyAI.Extensions;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Extracts the authenticated user id strictly from the token's
    /// <see cref="ClaimTypes.NameIdentifier"/> claim. This is the single choke
    /// point that prevents IDOR: callers can only ever act on their own id.
    /// </summary>
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var claim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(claim, out var userId))
        {
            return userId;
        }

        throw new UnauthorizedAccessException("Valid user id claim not found in token.");
    }

    public static bool TryGetUserId(this ClaimsPrincipal principal, out Guid userId)
    {
        userId = default;
        var claim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out userId);
    }

    // ADMIN_UNLIMITED: добавлено 2026-09-19
    /// <summary>True when the token's <c>isAdmin</c> claim is set to true.</summary>
    public static bool IsAdmin(this ClaimsPrincipal principal)
    {
        return bool.TryParse(principal.FindFirst("isAdmin")?.Value, out var isAdmin) && isAdmin;
    }
}
