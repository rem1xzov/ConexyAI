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
}
