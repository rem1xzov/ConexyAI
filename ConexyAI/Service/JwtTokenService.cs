using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Service.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ConexyAI.Service;

public class JwtTokenService : ITokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
    }

    // TOKEN_REVOCATION: добавлено 2026-09-24
    public TokenResponse CreateToken(User user) => CreateToken(user.Id, user.IsAdmin, user.TokenVersion);

    public TokenResponse CreateToken(Guid userId, bool isAdmin, int tokenVersion)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expiresAt = DateTime.UtcNow.AddMinutes(_options.AccessTokenLifetimeMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            // EMAIL_AUTH: добавлено 2026-09-19. TOKEN_REVOCATION 2026-09-24: значение в токене — лишь
            // снимок на момент выдачи; на каждом запросе оно заменяется значением из БД.
            new Claim(TokenRevocationValidator.IsAdminClaim, isAdmin ? "true" : "false", ClaimValueTypes.Boolean),
            // TOKEN_REVOCATION: добавлено 2026-09-24 (ревью M19)
            new Claim(TokenRevocationValidator.TokenVersionClaim,
                tokenVersion.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer32)
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: credentials);

        return new TokenResponse(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt,
            _options.AccessTokenLifetimeMinutes);
    }

    // GITHUB_OAUTH: добавлено 2026-09-19
    public ValidatedToken? ValidateToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _options.Issuer,
                ValidAudience = _options.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey))
            };

            var principal = handler.ValidateToken(token, validationParams, out var validatedToken);
            var expiresAt = (validatedToken as JwtSecurityToken)?.ValidTo;
            if (expiresAt is null)
            {
                return null;
            }

            return new ValidatedToken(
                new TokenResponse(token, expiresAt.Value, _options.AccessTokenLifetimeMinutes),
                principal);
        }
        catch
        {
            return null;
        }
    }
}
