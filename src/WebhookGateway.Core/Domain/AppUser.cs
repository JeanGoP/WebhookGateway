namespace WebhookGateway.Core.Domain;

/// <summary>
/// Usuario del panel. Single-tenant: son las pocas personas del equipo que administran
/// las integraciones, no clientes finales.
/// </summary>
public sealed class AppUser
{
    public int Id { get; set; }

    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Argon2id. Escribir hashing de contraseñas a mano es de las pocas cosas
    /// donde ahorrar código es mala idea.</summary>
    public required string PasswordHash { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsAdmin { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastLoginAt { get; set; }

    /// <summary>Intentos fallidos consecutivos. Se reinicia al entrar bien.</summary>
    public int FailedLoginCount { get; set; }

    /// <summary>Bloqueo temporal tras demasiados fallos.</summary>
    public DateTime? LockedUntil { get; set; }
}

/// <summary>
/// Token de refresco emitido a un usuario. Se guarda solo su hash: si alguien lee la
/// tabla, no obtiene tokens usables.
/// </summary>
public sealed class RefreshToken
{
    public long Id { get; set; }

    public int UserId { get; set; }

    public AppUser? User { get; set; }

    public required byte[] TokenHash { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public bool IsActive(DateTime now) => RevokedAt is null && now < ExpiresAt;
}

/// <summary>
/// Quién cambió qué configuración y cuándo. En un gateway que guarda credenciales de
/// terceros esto no es opcional.
/// </summary>
public sealed class AuditLog
{
    public long Id { get; set; }

    public DateTime OccurredAt { get; set; }

    public int? UserId { get; set; }

    public required string Action { get; set; }

    public required string EntityType { get; set; }

    public string? EntityId { get; set; }

    /// <summary>Resumen del cambio. Nunca incluye secretos, solo si cambiaron.</summary>
    public string? ChangesJson { get; set; }

    public string? SourceIp { get; set; }
}
