using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data;
using WebhookGateway.Data.Configuration;

namespace WebhookGateway.Api.Panel;

/// <summary>CRUD de integraciones: <c>/api/integrations</c>.</summary>
public static class IntegrationEndpoints
{
    public static void MapIntegrations(this WebApplication app)
    {
        var group = app.MapGroup("/api/integrations")
            .WithTags("Integrations")
            .RequireAuthorization();

        group.MapGet("/", ListAsync)
            .Produces<IReadOnlyList<IntegrationDto>>();

        group.MapGet("/{id:int}", GetAsync)
            .Produces<IntegrationDetailDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateAsync)
            .Produces<IntegrationDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        group.MapPut("/{id:int}", UpdateAsync)
            .Produces<IntegrationDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:int}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(GatewayDbContext db, CancellationToken ct)
    {
        var list = await db.Integrations
            .OrderBy(i => i.Name)
            .Select(i => new IntegrationDto(
                i.Id, i.Name, i.Slug, i.Description, i.IsActive,
                i.RetentionDays, i.PayloadRetentionDays, i.CreatedAt,
                i.OutboundEndpoints.Count == 0 ? "NoEndpoints" :
                i.OutboundEndpoints.Any(o => o.HealthState != null && o.HealthState.HealthStatus == 2) ? "Down" :
                i.OutboundEndpoints.Any(o => o.HealthState != null && o.HealthState.HealthStatus == 1) ? "Degraded" : "Healthy",
                i.OutboundEndpoints.Count,
                i.OutboundEndpoints.Count(o => o.HealthState == null || o.HealthState.HealthStatus == 0 || o.HealthState.HealthStatus == 3),
                i.OutboundEndpoints.Count(o => o.HealthState != null && o.HealthState.HealthStatus == 1),
                i.OutboundEndpoints.Count(o => o.HealthState != null && o.HealthState.HealthStatus == 2)))
            .ToListAsync(ct);

        return Results.Ok(list);
    }

    private static async Task<IResult> GetAsync(int id, GatewayDbContext db, CancellationToken ct)
    {
        var integration = await db.Integrations
            .Where(i => i.Id == id)
            .Select(i => new IntegrationDetailDto(
                i.Id, i.Name, i.Slug, i.Description, i.IsActive,
                i.RetentionDays, i.PayloadRetentionDays, i.CreatedAt,
                i.InboundEndpoints.Count(e => e.IsActive),
                i.OutboundEndpoints.Count(e => e.IsActive),
                i.InboundEndpoints.SelectMany(e => e.Subscriptions).Count(s => s.IsActive),
                i.OutboundEndpoints.Count == 0 ? "NoEndpoints" :
                i.OutboundEndpoints.Any(o => o.HealthState != null && o.HealthState.HealthStatus == 2) ? "Down" :
                i.OutboundEndpoints.Any(o => o.HealthState != null && o.HealthState.HealthStatus == 1) ? "Degraded" : "Healthy",
                i.OutboundEndpoints.Count,
                i.OutboundEndpoints.Count(o => o.HealthState == null || o.HealthState.HealthStatus == 0 || o.HealthState.HealthStatus == 3),
                i.OutboundEndpoints.Count(o => o.HealthState != null && o.HealthState.HealthStatus == 1),
                i.OutboundEndpoints.Count(o => o.HealthState != null && o.HealthState.HealthStatus == 2)))
            .FirstOrDefaultAsync(ct);

        return integration is null
            ? Results.NotFound(new ErrorResponse("Integración no encontrada."))
            : Results.Ok(integration);
    }

    private static async Task<IResult> CreateAsync(
        IntegrationRequest request, GatewayDbContext db, AuditService audit,
        HttpContext http, InboundConfigVersion inboundConfig, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Slug))
        {
            return Results.BadRequest(new ErrorResponse("Nombre y slug son obligatorios."));
        }

        var slug = request.Slug.Trim().ToLowerInvariant();

        if (await db.Integrations.AnyAsync(i => i.Slug == slug, ct))
        {
            return Results.Conflict(new ErrorResponse($"Ya existe una integración con el slug '{slug}'."));
        }

        var entity = new Integration
        {
            Name = request.Name.Trim(),
            Slug = slug,
            Description = request.Description?.Trim(),
            RetentionDays = request.RetentionDays ?? 365,
            PayloadRetentionDays = request.PayloadRetentionDays ?? 90,
        };

        db.Integrations.Add(entity);
        audit.Log(http.User, "create", "Integration", null,
            new { entity.Name, entity.Slug }, PanelHelpers.ClientIp(http));
        await db.SaveChangesAsync(ct);
        inboundConfig.Invalidate();

        return Results.Created(
            $"/api/integrations/{entity.Id.ToString(CultureInfo.InvariantCulture)}",
            entity.ToListDto());
    }

    private static async Task<IResult> UpdateAsync(
        int id, IntegrationRequest request, GatewayDbContext db, AuditService audit,
        HttpContext http, InboundConfigVersion inboundConfig, CancellationToken ct)
    {
        var entity = await db.Integrations.AsTracking().FirstOrDefaultAsync(i => i.Id == id, ct);

        if (entity is null)
        {
            return Results.NotFound(new ErrorResponse("Integración no encontrada."));
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            entity.Name = request.Name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            entity.Description = request.Description.Trim();
        }

        if (request.IsActive is not null)
        {
            entity.IsActive = request.IsActive.Value;
        }

        if (request.RetentionDays is not null)
        {
            entity.RetentionDays = request.RetentionDays.Value;
        }

        if (request.PayloadRetentionDays is not null)
        {
            entity.PayloadRetentionDays = request.PayloadRetentionDays.Value;
        }

        audit.Log(http.User, "update", "Integration", id.ToString(CultureInfo.InvariantCulture),
            new { request.Name, request.IsActive }, PanelHelpers.ClientIp(http));
        await db.SaveChangesAsync(ct);
        inboundConfig.Invalidate();

        return Results.Ok(entity.ToListDto());
    }

    private static async Task<IResult> DeleteAsync(
        int id, GatewayDbContext db, AuditService audit, HttpContext http, InboundConfigVersion inboundConfig, CancellationToken ct)
    {
        var entity = await db.Integrations.AsTracking().FirstOrDefaultAsync(i => i.Id == id, ct);

        if (entity is null)
        {
            return Results.NotFound(new ErrorResponse("Integración no encontrada."));
        }

        // Desactivar, no borrar: las entregas existentes la referencian.
        entity.IsActive = false;

        audit.Log(http.User, "deactivate", "Integration", id.ToString(CultureInfo.InvariantCulture),
            null, PanelHelpers.ClientIp(http));
        await db.SaveChangesAsync(ct);
        inboundConfig.Invalidate();

        return Results.NoContent();
    }
}
