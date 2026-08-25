using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data;

namespace WebhookGateway.Api.Panel;

/// <summary>CRUD de suscripciones (fanout): <c>/api/subscriptions</c>.</summary>
public static class SubscriptionEndpoints
{
    public static void MapSubscriptions(this WebApplication app)
    {
        var group = app.MapGroup("/api/subscriptions")
            .WithTags("Subscriptions")
            .RequireAuthorization();

        group.MapGet("/", ListAsync)
            .Produces<IReadOnlyList<SubscriptionDto>>();

        group.MapPost("/", CreateAsync)
            .Produces<SubscriptionDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        group.MapPut("/{id:int}", UpdateAsync)
            .Produces<SubscriptionDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:int}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(
        int? inboundEndpointId, int? outboundEndpointId,
        GatewayDbContext db, CancellationToken ct)
    {
        var query = db.Subscriptions
            .Include(s => s.InboundEndpoint)
            .Include(s => s.OutboundEndpoint)
            .AsQueryable();

        if (inboundEndpointId is not null)
        {
            query = query.Where(s => s.InboundEndpointId == inboundEndpointId.Value);
        }

        if (outboundEndpointId is not null)
        {
            query = query.Where(s => s.OutboundEndpointId == outboundEndpointId.Value);
        }

        var list = await query
            .OrderBy(s => s.Id)
            .Select(s => s.ToDto())
            .ToListAsync(ct);

        return Results.Ok(list);
    }

    private static async Task<IResult> CreateAsync(
        SubscriptionRequest request, GatewayDbContext db,
        AuditService audit, HttpContext http, CancellationToken ct)
    {
        if (request.InboundEndpointId is null || request.OutboundEndpointId is null)
        {
            return Results.BadRequest(new ErrorResponse(
                "inboundEndpointId y outboundEndpointId son obligatorios."));
        }

        if (!await db.InboundEndpoints.AnyAsync(e => e.Id == request.InboundEndpointId, ct))
        {
            return Results.NotFound(new ErrorResponse("Endpoint de entrada no encontrado."));
        }

        if (!await db.OutboundEndpoints.AnyAsync(e => e.Id == request.OutboundEndpointId, ct))
        {
            return Results.NotFound(new ErrorResponse("Endpoint de salida no encontrado."));
        }

        if (await db.Subscriptions.AnyAsync(s =>
            s.InboundEndpointId == request.InboundEndpointId &&
            s.OutboundEndpointId == request.OutboundEndpointId, ct))
        {
            return Results.Conflict(new ErrorResponse("Esta suscripción ya existe."));
        }

        var entity = new Subscription
        {
            InboundEndpointId = request.InboundEndpointId.Value,
            OutboundEndpointId = request.OutboundEndpointId.Value,
        };

        db.Subscriptions.Add(entity);
        audit.Log(http.User, "create", "Subscription", null,
            new { request.InboundEndpointId, request.OutboundEndpointId },
            PanelHelpers.ClientIp(http));
        await db.SaveChangesAsync(ct);

        // Cargar las navegaciones para el DTO de respuesta.
        await db.Entry(entity).Reference(s => s.InboundEndpoint).LoadAsync(ct);
        await db.Entry(entity).Reference(s => s.OutboundEndpoint).LoadAsync(ct);

        return Results.Created(
            $"/api/subscriptions/{entity.Id.ToString(CultureInfo.InvariantCulture)}",
            entity.ToDto());
    }

    private static async Task<IResult> UpdateAsync(
        int id, SubscriptionUpdateRequest request, GatewayDbContext db,
        AuditService audit, HttpContext http, CancellationToken ct)
    {
        var entity = await db.Subscriptions.AsTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

        if (entity is null)
        {
            return Results.NotFound(new ErrorResponse("Suscripción no encontrada."));
        }

        if (request.IsActive is not null)
        {
            entity.IsActive = request.IsActive.Value;
        }

        audit.Log(http.User, "update", "Subscription", id.ToString(CultureInfo.InvariantCulture),
            new { request.IsActive }, PanelHelpers.ClientIp(http));
        await db.SaveChangesAsync(ct);

        return Results.Ok(entity.ToDto());
    }

    private static async Task<IResult> DeleteAsync(
        int id, GatewayDbContext db, AuditService audit, HttpContext http, CancellationToken ct)
    {
        var entity = await db.Subscriptions.AsTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

        if (entity is null)
        {
            return Results.NotFound(new ErrorResponse("Suscripción no encontrada."));
        }

        // Desactivar en vez de borrar: las entregas existentes la referencian.
        entity.IsActive = false;

        audit.Log(http.User, "deactivate", "Subscription", id.ToString(CultureInfo.InvariantCulture),
            null, PanelHelpers.ClientIp(http));
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}
