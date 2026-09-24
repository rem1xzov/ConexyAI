using System.Globalization;
using System.Security.Claims;
using ConexyAI.Extensions;
using ConexyAI.Repository;

namespace ConexyAI.Service.Auth;

// TOKEN_REVOCATION: добавлено 2026-09-24 (ревью M19)
/// <summary>Outcome of <see cref="ITokenRevocationValidator.CheckAsync"/>.</summary>
public sealed record TokenCheckResult(bool IsValid, string? FailureReason, UserAuthState? State)
{
    public static TokenCheckResult Valid(UserAuthState state) => new(true, null, state);
    public static TokenCheckResult Invalid(string reason) => new(false, reason, null);
}

/// <summary>
/// Checks an already signature-validated JWT principal against the database: the user must still
/// exist and the token's <c>tv</c> claim must equal <c>User.TokenVersion</c>.
/// </summary>
public interface ITokenRevocationValidator
{
    Task<TokenCheckResult> CheckAsync(ClaimsPrincipal principal, CancellationToken ct = default);
}

public sealed class TokenRevocationValidator : ITokenRevocationValidator
{
    /// <summary>JWT claim carrying <c>User.TokenVersion</c> at issue time.</summary>
    public const string TokenVersionClaim = "tv";

    /// <summary>JWT claim carrying the admin flag (see <c>ClaimsPrincipalExtensions.IsAdmin</c>).</summary>
    public const string IsAdminClaim = "isAdmin";

    public const string ReasonNoUserId = "no_user_id";
    public const string ReasonLegacyToken = "legacy_token_without_tv";
    public const string ReasonUserNotFound = "user_not_found";
    public const string ReasonRevoked = "token_revoked";

    private readonly IUserRepository _users;
    private readonly IUserAuthStateCache _cache;

    public TokenRevocationValidator(IUserRepository users, IUserAuthStateCache cache)
    {
        _users = users;
        _cache = cache;
    }

    public async Task<TokenCheckResult> CheckAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (!principal.TryGetUserId(out var userId))
            return TokenCheckResult.Invalid(ReasonNoUserId);

        // Токены без tv (выданы до этого релиза) ОТКЛОНЯЮТСЯ: так гарантированно умирают все
        // 30-дневные токены, успевшие попасть в access-логи nginx через ?access_token= (ревью L12).
        // Цена — однократный повторный вход у всех пользователей после деплоя.
        var raw = principal.FindFirst(TokenVersionClaim)?.Value;
        if (raw is null || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var tokenVersion))
            return TokenCheckResult.Invalid(ReasonLegacyToken);

        var state = await _cache.GetOrLoadAsync(userId, c => _users.GetAuthStateAsync(userId, c), ct);
        if (state is null)
            return TokenCheckResult.Invalid(ReasonUserNotFound);

        return state.TokenVersion == tokenVersion
            ? TokenCheckResult.Valid(state)
            : TokenCheckResult.Invalid(ReasonRevoked);
    }

    /// <summary>
    /// Replaces every <c>isAdmin</c> claim of <paramref name="principal"/> with the database value,
    /// so a demoted admin loses admin rights on the next request even with an old token (and a
    /// promoted user gains them without re-login).
    /// </summary>
    public static void ApplyAdminClaim(ClaimsPrincipal principal, bool isAdmin)
    {
        ClaimsIdentity? target = null;
        foreach (var identity in principal.Identities)
        {
            target ??= identity;
            foreach (var claim in identity.FindAll(IsAdminClaim).ToList())
                identity.TryRemoveClaim(claim);
        }

        target?.AddClaim(new Claim(IsAdminClaim, isAdmin ? "true" : "false", ClaimValueTypes.Boolean));
    }
}
