using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Api.Auth;

/// <summary>
/// Genera access tokens JWT y refresh tokens opacos. El refresh token se devuelve en claro
/// al usuario y se guarda como hash SHA-256 en la base de datos: si alguien lee la tabla,
/// no obtiene tokens usables.
/// </summary>
public sealed class JwtTokenGenerator(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;

    private readonly SigningCredentials _credentials = new(
        new SymmetricSecurityKey(Convert.FromBase64String(options.Value.Key)),
        SecurityAlgorithms.HmacSha256);

    public string GenerateAccessToken(AppUser user)
    {
        var now = clock.GetUtcNow();

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim("name", user.DisplayName),
                new Claim("admin", user.IsAdmin.ToString().ToLowerInvariant()),
            ]),
            Expires = now.AddMinutes(_options.AccessTokenMinutes).UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            SigningCredentials = _credentials,
        };

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }

    /// <summary>Genera un refresh token opaco y devuelve el token en claro + su hash para la BD.</summary>
    public (string Token, byte[] Hash, DateTime ExpiresAt) GenerateRefreshToken()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes);
        var hash = SHA256.HashData(tokenBytes);
        var expiresAt = clock.GetUtcNow().AddDays(_options.RefreshTokenDays).UtcDateTime;

        return (token, hash, expiresAt);
    }

    /// <summary>Hash SHA-256 de un refresh token en claro, para buscarlo en la BD.</summary>
    public static byte[] HashRefreshToken(string token) =>
        SHA256.HashData(Convert.FromBase64String(token));
}
