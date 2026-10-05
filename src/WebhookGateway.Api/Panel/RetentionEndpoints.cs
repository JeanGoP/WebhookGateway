using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data;

namespace WebhookGateway.Api.Panel;

public sealed record RetentionPolicyRequest(int MetadataDays, int PayloadDays, int AttemptDays);

/// <param name="LongestDeliveryWindowHours">
/// La ventana de entrega más larga de los destinos: por debajo de eso no se pueden guardar los cuerpos.
/// </param>
public sealed record RetentionPolicyDto(
    int MetadataDays, int PayloadDays, int AttemptDays,
    int LongestDeliveryWindowHours, DateTime UpdatedAt, string? UpdatedBy);

/// <summary>
/// La retención, editable desde el panel: <c>/api/settings/retention</c>. La purga nocturna la lee en
/// cada ejecución, así que un cambio surte efecto la noche siguiente.
/// </summary>
public static class RetentionEndpoints
{
    public static void MapRetention(this WebApplication app)
    {
        var group = app.MapGroup("/api/settings/retention")
            .WithTags("Settings")
            .RequireAuthorization();

        group.MapGet("/", GetAsync)
            .Produces<RetentionPolicyDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPut("/", UpdateAsync)
            .Produces<RetentionPolicyDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> GetAsync(GatewayDbContext db, CancellationToken ct)
    {
        var policy = await db.RetentionPolicies.FirstOrDefaultAsync(ct);

        return policy is null
            ? NotConfigured()
            : Results.Ok(policy.ToDto(await LongestWindowHoursAsync(db, ct)));
    }

    /// <summary>
    /// Solo administradores: lo que se cambia aquí decide qué borra la purga, y lo borrado no vuelve.
    /// </summary>
    private static async Task<IResult> UpdateAsync(
        RetentionPolicyRequest request, GatewayDbContext db, AuditService audit,
        HttpContext http, TimeProvider clock, CancellationToken ct)
    {
        if (!PanelHelpers.IsAdmin(http.User))
        {
            return Results.Forbid();
        }

        var longestWindowHours = await LongestWindowHoursAsync(db, ct);

        if (RetentionPolicy.Check(request.MetadataDays, request.PayloadDays, request.AttemptDays, longestWindowHours)
            is { } error)
        {
            return Results.BadRequest(new ErrorResponse(error));
        }

        var policy = await db.RetentionPolicies.AsTracking().FirstOrDefaultAsync(ct);

        if (policy is null)
        {
            return NotConfigured();
        }

        var before = new { policy.MetadataDays, policy.PayloadDays, policy.AttemptDays };

        policy.MetadataDays = request.MetadataDays;
        policy.PayloadDays = request.PayloadDays;
        policy.AttemptDays = request.AttemptDays;
        policy.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        // Según cómo traduzca el middleware los claims del token, el correo llega con un nombre u otro.
        policy.UpdatedBy = http.User.FindFirstValue(ClaimTypes.Email)
                           ?? http.User.FindFirstValue(JwtRegisteredClaimNames.Email);

        audit.Log(http.User, "update", "RetentionPolicy", "1",
            new { Antes = before, Despues = request }, PanelHelpers.ClientIp(http));
        await db.SaveChangesAsync(ct);

        return Results.Ok(policy.ToDto(longestWindowHours));
    }

    private static async Task<int> LongestWindowHoursAsync(GatewayDbContext db, CancellationToken ct) =>
        await db.OutboundEndpoints.MaxAsync(e => (int?)e.DeliveryWindowHours, ct) ?? 0;

    private static IResult NotConfigured() =>
        Results.NotFound(new ErrorResponse(
            "No hay política de retención en la base. Aplica db/17-retention-policy.sql."));

    private static RetentionPolicyDto ToDto(this RetentionPolicy p, int longestWindowHours) =>
        new(p.MetadataDays, p.PayloadDays, p.AttemptDays, longestWindowHours, p.UpdatedAt, p.UpdatedBy);
}
