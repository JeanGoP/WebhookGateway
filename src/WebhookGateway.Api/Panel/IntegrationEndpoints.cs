using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data;

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
            .Select(i => i.ToListDto())
            .ToListAsync(ct);

        return Results.Ok(list);
    }

    private static async Task<IResult> GetAsync(int id, GatewayDbContext db, CancellationToken ct)
    {
        var integration = await db.Integrations
            .Where(i => i.Id == id)
            .Select(i => i.ToDetailDto(
                i.InboundEndpoints.Count(e => e.IsActive),
                i.OutboundEndpoints.Count(e => e.IsActive),
                i.InboundEndpoints.SelectMany(e => e.Subscriptions).Count(s => s.IsActive)))
            .FirstOrDefaultAsync(ct);

        return integration is null
            ? Results.NotFound(new ErrorResponse("Integración no encontrada."))
            : Results.Ok(integration);
    }

    private static async Task<IResult> CreateAsync(
        IntegrationRequest request, GatewayDbContext db, AuditService audit,
        HttpContext http, CancellationToken ct)
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

        return Results.Created(
            $"/api/integrations/{entity.Id.ToString(CultureInfo.InvariantCulture)}",
            entity.ToListDto());
    }

    private static async Task<IResult> UpdateAsync(
        int id, IntegrationRequest request, GatewayDbContext db, AuditService audit,
        HttpContext http, CancellationToken ct)
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

        return Results.Ok(entity.ToListDto());
    }

    private static async Task<IResult> DeleteAsync(
        int id, GatewayDbContext db, AuditService audit, HttpContext http, CancellationToken ct)
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

        return Results.NoContent();
    }
}
