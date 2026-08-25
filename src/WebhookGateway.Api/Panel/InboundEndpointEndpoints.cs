using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data;
using WebhookGateway.Data.Security;

namespace WebhookGateway.Api.Panel;

/// <summary>CRUD de endpoints de entrada: <c>/api/integrations/{integrationId}/inbound</c>.</summary>
public static class InboundEndpointEndpoints
{
    public static void MapInboundEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/integrations/{integrationId:int}/inbound")
            .WithTags("InboundEndpoints")
            .RequireAuthorization();

        group.MapGet("/", ListAsync)
            .Produces<IReadOnlyList<InboundEndpointDto>>();

        group.MapGet("/{id:int}", GetAsync)
            .Produces<InboundEndpointDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateAsync)
            .Produces<InboundEndpointDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        group.MapPut("/{id:int}", UpdateAsync)
            .Produces<InboundEndpointDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(int integrationId, GatewayDbContext db, CancellationToken ct)
    {
        var list = await db.InboundEndpoints
            .Where(e => e.IntegrationId == integrationId)
            .OrderBy(e => e.Name)
            .Select(e => e.ToDto())
            .ToListAsync(ct);

        return Results.Ok(list);
    }

    private static async Task<IResult> GetAsync(int integrationId, int id, GatewayDbContext db, CancellationToken ct)
    {
        var entity = await db.InboundEndpoints
            .Where(e => e.Id == id && e.IntegrationId == integrationId)
            .Select(e => e.ToDto())
            .FirstOrDefaultAsync(ct);

        return entity is null
            ? Results.NotFound(new ErrorResponse("Endpoint de entrada no encontrado."))
            : Results.Ok(entity);
    }

    private static async Task<IResult> CreateAsync(
        int integrationId, InboundEndpointRequest request, GatewayDbContext db,
        AuthConfigCodec codec, AuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Slug))
        {
            return Results.BadRequest(new ErrorResponse("Nombre y slug son obligatorios."));
        }

        if (!await db.Integrations.AnyAsync(i => i.Id == integrationId, ct))
        {
            return Results.NotFound(new ErrorResponse("Integración no encontrada."));
        }

        var slug = request.Slug.Trim().ToLowerInvariant();

        if (await db.InboundEndpoints.AnyAsync(e => e.IntegrationId == integrationId && e.Slug == slug, ct))
        {
            return Results.Conflict(new ErrorResponse(
                $"Ya existe un endpoint de entrada con el slug '{slug}' en esta integración."));
        }

        var authType = request.AuthType ?? InboundAuthType.None;

        var entity = new InboundEndpoint
        {
            IntegrationId = integrationId,
            Name = request.Name.Trim(),
            Slug = slug,
            AuthType = authType,
            DedupeStrategy = request.DedupeStrategy ?? DedupeStrategy.None,
            DedupeSource = request.DedupeSource?.Trim(),
            MaxBodyBytes = request.MaxBodyBytes ?? 1024 * 1024,
        };

        if (request.AuthConfig is { ValueKind: not JsonValueKind.Null } authJson
            && authType != InboundAuthType.None)
        {
            entity.AuthConfig = codec.Encode(authJson, authType);
        }

        db.InboundEndpoints.Add(entity);
        audit.Log(http.User, "create", "InboundEndpoint", null,
            new { entity.Name, entity.Slug, entity.AuthType }, PanelHelpers.ClientIp(http));
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/integrations/{integrationId.ToString(CultureInfo.InvariantCulture)}/inbound/{entity.Id.ToString(CultureInfo.InvariantCulture)}",
            entity.ToDto());
    }

    private static async Task<IResult> UpdateAsync(
        int integrationId, int id, InboundEndpointRequest request, GatewayDbContext db,
        AuthConfigCodec codec, AuditService audit, HttpContext http, CancellationToken ct)
    {
        var entity = await db.InboundEndpoints.AsTracking()
            .FirstOrDefaultAsync(e => e.Id == id && e.IntegrationId == integrationId, ct);

        if (entity is null)
        {
            return Results.NotFound(new ErrorResponse("Endpoint de entrada no encontrado."));
        }

        request.ApplyTo(entity, codec);

        audit.Log(http.User, "update", "InboundEndpoint", id.ToString(CultureInfo.InvariantCulture),
            new { request.Name, request.IsActive, request.AuthType }, PanelHelpers.ClientIp(http));
        await db.SaveChangesAsync(ct);

        return Results.Ok(entity.ToDto());
    }
}
