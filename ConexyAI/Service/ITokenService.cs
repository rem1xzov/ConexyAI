using System.Security.Claims;
using ConexyAI.Contract;
using ConexyAI.Entity;

namespace ConexyAI.Service;

public interface ITokenService
{
    /// <summary>
    /// Issues a signed JWT whose <c>sub</c> claim (exposed to the app as
    /// <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>) carries the
    /// authenticated user id.
    /// </summary>
    /// <remarks>
    /// TOKEN_REVOCATION: добавлено 2026-09-24 (ревью M19) — <paramref name="tokenVersion"/> обязателен
    /// и попадает в claim <c>tv</c>; токен живёт, пока он равен <c>User.TokenVersion</c> в БД.
    /// Поэтому версия берётся только из свежепрочитанной строки пользователя — см. перегрузку
    /// <see cref="CreateToken(User)"/>.
    /// </remarks>
    TokenResponse CreateToken(Guid userId, bool isAdmin, int tokenVersion);

    /// <summary>Issues a JWT for <paramref name="user"/> (its id, admin flag and token version).</summary>
    TokenResponse CreateToken(User user);

    // GITHUB_OAUTH: добавлено 2026-09-19
    /// <summary>
    /// Validates a JWT (signature, issuer, audience, lifetime) and returns its expiry and claims
    /// when valid; otherwise <c>null</c>. Used by <c>/api/auth/session</c> and logout, which read
    /// the token from the httpOnly cookie. This does NOT check revocation — callers pair it with
    /// <c>ITokenRevocationValidator</c>.
    /// </summary>
    ValidatedToken? ValidateToken(string token);
}

// TOKEN_REVOCATION: добавлено 2026-09-24
/// <summary>A signature/lifetime-valid token: the response to hand back plus its claims.</summary>
public sealed record ValidatedToken(TokenResponse Response, ClaimsPrincipal Principal);
