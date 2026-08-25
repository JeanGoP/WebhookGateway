using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookGateway.Core.Common;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data;
using WebhookGateway.Data.Security;

namespace WebhookGateway.Api.Auth;

/// <summary>
/// Login, refresh y logout. Single-tenant: las pocas personas del equipo que administran
/// integraciones, no clientes finales.
/// </summary>
public sealed class AuthService(
    GatewayDbContext db,
    JwtTokenGenerator jwt,
    IOptions<JwtOptions> options,
    TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;

    public async Task<Result<AuthResponse>> LoginAsync(string email, string password, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null)
        {
            return Result.Fail<AuthResponse>("auth.invalid", "Credenciales incorrectas.");
        }

        if (!user.IsActive)
        {
            return Result.Fail<AuthResponse>("auth.disabled", "La cuenta está desactivada.");
        }

        var now = clock.GetUtcNow().UtcDateTime;

        if (user.LockedUntil is not null && now < user.LockedUntil)
        {
            return Result.Fail<AuthResponse>("auth.locked",
                $"Cuenta bloqueada temporalmente. Intenta de nuevo en {(int)(user.LockedUntil.Value - now).TotalMinutes + 1} minutos.");
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash))
        {
            db.Attach(user);
            user.FailedLoginCount++;

            if (user.FailedLoginCount >= _options.MaxFailedAttempts)
            {
                user.LockedUntil = now.AddMinutes(_options.LockoutMinutes);
            }

            await db.SaveChangesAsync(ct);
            return Result.Fail<AuthResponse>("auth.invalid", "Credenciales incorrectas.");
        }

        // Login correcto: limpiar contadores y actualizar último acceso.
        db.Attach(user);
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;

        var (refreshToken, refreshHash, refreshExpires) = jwt.GenerateRefreshToken();

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshHash,
            ExpiresAt = refreshExpires,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);

        return Result.Ok(new AuthResponse(
            jwt.GenerateAccessToken(user),
            refreshToken,
            user.Email,
            user.DisplayName,
            user.IsAdmin));
    }

    public async Task<Result<AuthResponse>> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var hash = JwtTokenGenerator.HashRefreshToken(refreshToken);
        var now = clock.GetUtcNow().UtcDateTime;

        var stored = await db.RefreshTokens
            .AsTracking()
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.TokenHash == hash, ct);

        if (stored is null || !stored.IsActive(now) || stored.User is null || !stored.User.IsActive)
        {
            return Result.Fail<AuthResponse>("auth.invalid_refresh", "Token de refresco inválido o expirado.");
        }

        // Rotación: revocar el usado y emitir uno nuevo. Así, un refresh token robado deja
        // de servir en cuanto el legítimo lo usa primero.
        stored.RevokedAt = now;

        var (newRefreshToken, newHash, newExpires) = jwt.GenerateRefreshToken();

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = stored.UserId,
            TokenHash = newHash,
            ExpiresAt = newExpires,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);

        return Result.Ok(new AuthResponse(
            jwt.GenerateAccessToken(stored.User),
            newRefreshToken,
            stored.User.Email,
            stored.User.DisplayName,
            stored.User.IsAdmin));
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        var hash = JwtTokenGenerator.HashRefreshToken(refreshToken);

        var stored = await db.RefreshTokens
            .AsTracking()
            .FirstOrDefaultAsync(r => r.TokenHash == hash, ct);

        if (stored is not null && stored.RevokedAt is null)
        {
            stored.RevokedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }
    }
}

public sealed record AuthResponse(
    string AccessToken,
    string RefreshToken,
    string Email,
    string DisplayName,
    bool IsAdmin);
