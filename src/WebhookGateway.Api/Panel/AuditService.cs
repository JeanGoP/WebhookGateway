using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data;

namespace WebhookGateway.Api.Panel;

/// <summary>
/// Registra quién cambió qué configuración. En un gateway que custodia credenciales de
/// terceros, la auditoría no es opcional.
/// </summary>
/// <remarks>
/// No es un interceptor de EF: se llama explícitamente. Así el que escribe el endpoint
/// decide qué se registra y qué no, y <c>ChangesJson</c> nunca incluye secretos por
/// accidente.
/// </remarks>
public sealed class AuditService(GatewayDbContext db, TimeProvider clock)
{
    public void Log(ClaimsPrincipal user, string action, string entityType, string? entityId,
        object? changes = null, string? sourceIp = null)
    {
        var userId = int.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : (int?)null;

        db.AuditLogs.Add(new AuditLog
        {
            OccurredAt = clock.GetUtcNow().UtcDateTime,
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            ChangesJson = changes is null ? null : JsonSerializer.Serialize(changes, JsonOptions.Audit),
            SourceIp = sourceIp,
        });
    }
}

/// <summary>Opciones de serialización compartidas por el panel.</summary>
internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Audit = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
