using System.Globalization;
using WebhookGateway.Data;
using WebhookGateway.Data.Traffic;

namespace WebhookGateway.Api.Panel;

/// <summary>Intentos y reenvío manual de una entrega: <c>/api/deliveries</c>.</summary>
public static class DeliveryEndpoints
{
    public static void MapDeliveries(this WebApplication app)
    {
        var group = app.MapGroup("/api/deliveries")
            .WithTags("Deliveries")
            .RequireAuthorization();

        group.MapGet("/{id:long}/attempts", GetAttemptsAsync)
            .Produces<IReadOnlyList<AttemptDto>>();

        group.MapPost("/{id:long}/retry", RetryAsync)
            .Produces<DeliveryRetryResult>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> GetAttemptsAsync(
        long id, MessageExplorer explorer, CancellationToken ct)
    {
        var attempts = await explorer.GetAttemptsAsync(id, ct);

        return Results.Ok(attempts.Select(a => a.ToDto()).ToList());
    }

    /*
        El reenvío crea una entrega nueva, no reabre la vieja. El historial de intentos de
        la original es la prueba de lo que pasó, y reescribirlo dejaría al panel sin poder
        explicar por qué se reintentó.
    */
    private static async Task<IResult> RetryAsync(
        long id, DeliveryRetryWriter retry, GatewayDbContext db, AuditService audit,
        HttpContext http, CancellationToken ct)
    {
        var result = await retry.RetryAsync(id, ct);

        if (!result.TryGetValue(out var created))
        {
            return result.Error.Code == "delivery.not_found"
                ? Results.NotFound(new ErrorResponse(result.Error.Message))
                : Results.Conflict(new ErrorResponse(result.Error.Message));
        }

        audit.Log(http.User, "retry", "WebhookDelivery",
            id.ToString(CultureInfo.InvariantCulture),
            new { created.NewDeliveryId }, PanelHelpers.ClientIp(http));

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/deliveries/{created.NewDeliveryId.ToString(CultureInfo.InvariantCulture)}/attempts",
            created);
    }
}
