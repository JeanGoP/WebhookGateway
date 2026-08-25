namespace WebhookGateway.Api.Auth;

/// <summary>
/// Configuración JWT leída de <c>Gateway:Jwt</c>. La clave simétrica la genera el usuario
/// y debe ser de al menos 32 bytes en base64.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Gateway:Jwt";

    /// <summary>Clave simétrica en base64 (mínimo 256 bits).</summary>
    public required string Key { get; init; }

    /// <summary>Emisor del token. Se valida en cada petición.</summary>
    public string Issuer { get; init; } = "WebhookGateway";

    /// <summary>Audiencia del token. Se valida en cada petición.</summary>
    public string Audience { get; init; } = "WebhookGateway";

    /// <summary>Minutos de vida del access token. Corto a propósito.</summary>
    public int AccessTokenMinutes { get; init; } = 30;

    /// <summary>Días de vida del refresh token.</summary>
    public int RefreshTokenDays { get; init; } = 7;

    /// <summary>Intentos de login fallidos antes de bloquear la cuenta temporalmente.</summary>
    public int MaxFailedAttempts { get; init; } = 5;

    /// <summary>Minutos de bloqueo tras superar los intentos fallidos.</summary>
    public int LockoutMinutes { get; init; } = 15;
}
