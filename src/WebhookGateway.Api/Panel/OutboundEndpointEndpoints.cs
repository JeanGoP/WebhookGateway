using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data;
using WebhookGateway.Data.Security;

namespace WebhookGateway.Api.Panel;

/// <summary>CRUD de endpoints de salida: <c>/api/integrations/{integrationId}/outbound</c>.</summary>
public static class OutboundEndpointEndpoints
{
    public static void MapOutboundEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/integrations/{integrationId:int}/outbound")
            .WithTags("OutboundEndpoints")
            .RequireAuthorization();

        group.MapGet("/", ListAsync)
            .Produces<IReadOnlyList<OutboundEndpointDto>>();

        group.MapGet("/{id:int}", GetAsync)
            .Produces<OutboundEndpointDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateAsync)
            .Produces<OutboundEndpointDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPut("/{id:int}", UpdateAsync)
            .Produces<OutboundEndpointDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(int integrationId, GatewayDbContext db, CancellationToken ct)
    {
        var list = await db.OutboundEndpoints
            .Where(e => e.IntegrationId == integrationId)
            .OrderBy(e => e.Name)
            .Select(e => e.ToDto())
            .ToListAsync(ct);

        return Results.Ok(list);
    }

    private static async Task<IResult> GetAsync(int integrationId, int id, GatewayDbContext db, CancellationToken ct)
    {
        var entity = await db.OutboundEndpoints
            .Where(e => e.Id == id && e.IntegrationId == integrationId)
            .Select(e => e.ToDto())
            .FirstOrDefaultAsync(ct);

        return entity is null
            ? Results.NotFound(new ErrorResponse("Endpoint de salida no encontrado."))
            : Results.Ok(entity);
    }

    private static async Task<IResult> CreateAsync(
        int integrationId, OutboundEndpointRequest request, GatewayDbContext db,
        AuthConfigCodec codec, AuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.TargetUrl))
        {
            return Results.BadRequest(new ErrorResponse("Nombre y URL de destino son obligatorios."));
        }

        if (!await db.Integrations.AnyAsync(i => i.Id == integrationId, ct))
        {
            return Results.NotFound(new ErrorResponse("Integración no encontrada."));
        }

        var authType = request.AuthType ?? OutboundAuthType.None;

        var entity = new OutboundEndpoint
        {
            IntegrationId = integrationId,
            Name = request.Name.Trim(),
            TargetUrl = request.TargetUrl.Trim(),
            HttpMethod = request.HttpMethod?.Trim().ToUpperInvariant() ?? "POST",
            AuthType = authType,
            CustomHeadersJson = request.CustomHeadersJson,
            RateLimitPerMinute = request.RateLimitPerMinute ?? 600,
            MaxConcurrency = request.MaxConcurrency ?? 4,
            TimeoutSeconds = request.TimeoutSeconds ?? 30,
            MaxAttempts = request.MaxAttempts ?? 8,
            DeliveryWindowHours = request.DeliveryWindowHours ?? 72,
            BackoffLadderJson = request.BackoffLadderJson,
            BreakerFailureThreshold = request.BreakerFailureThreshold ?? 5,
            BreakerOpenSeconds = request.BreakerOpenSeconds ?? 60,
        };

        if (request.AuthConfig is { ValueKind: not JsonValueKind.Null } authJson
            && authType != OutboundAuthType.None)
        {
            entity.AuthConfig = codec.Encode(authJson, authType);
        }

        db.OutboundEndpoints.Add(entity);
        audit.Log(http.User, "create", "OutboundEndpoint", null,
            new { entity.Name, entity.TargetUrl, entity.AuthType }, PanelHelpers.ClientIp(http));
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/integrations/{integrationId.ToString(CultureInfo.InvariantCulture)}/outbound/{entity.Id.ToString(CultureInfo.InvariantCulture)}",
            entity.ToDto());
    }

    private static async Task<IResult> UpdateAsync(
        int integrationId, int id, OutboundEndpointRequest request, GatewayDbContext db,
        AuthConfigCodec codec, AuditService audit, HttpContext http, CancellationToken ct)
    {
        var entity = await db.OutboundEndpoints.AsTracking()
            .FirstOrDefaultAsync(e => e.Id == id && e.IntegrationId == integrationId, ct);

        if (entity is null)
        {
            return Results.NotFound(new ErrorResponse("Endpoint de salida no encontrado."));
        }

        request.ApplyTo(entity, codec);

        audit.Log(http.User, "update", "OutboundEndpoint", id.ToString(CultureInfo.InvariantCulture),
            new { request.Name, request.IsActive, request.AuthType, request.TargetUrl },
            PanelHelpers.ClientIp(http));
        await db.SaveChangesAsync(ct);

        return Results.Ok(entity.ToDto());
    }
}
