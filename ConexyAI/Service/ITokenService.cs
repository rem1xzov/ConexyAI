using ConexyAI.Contract;

namespace ConexyAI.Service;

public interface ITokenService
{
    /// <summary>
    /// Issues a signed JWT whose <c>sub</c> claim (exposed to the app as
    /// <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>) carries the
    /// authenticated user id.
    /// </summary>
    TokenResponse CreateToken(Guid userId);

    // GITHUB_OAUTH: добавлено 2026-09-19
    /// <summary>
    /// Validates a JWT (signature, issuer, audience, lifetime) and returns a
    /// <see cref="TokenResponse"/> carrying its expiry when valid; otherwise <c>null</c>.
    /// Used by <c>/api/auth/session</c> to hand the frontend a fresh, verified token.
    /// </summary>
    TokenResponse? ValidateToken(string token);
}
