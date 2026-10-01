using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookGateway.Core.Domain;
using WebhookGateway.Core.Notifications;
using WebhookGateway.Data;
using WebhookGateway.Dispatcher.Notifications;

namespace WebhookGateway.Api.Panel;

public static class SubscriberEndpoints
{
    public static void MapSubscribers(this WebApplication app)
    {
        var group = app.MapGroup("/api/integrations/{integrationId:int}/subscribers")
            .WithTags("Subscribers")
            .RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", AddAsync);
        group.MapDelete("/{subscriberId:int}", DeleteAsync);
        group.MapPost("/{subscriberId:int}/toggle-mute", ToggleMuteAsync);
        group.MapPost("/{subscriberId:int}/resend", ResendVerificationAsync);
    }

    private static async Task<IResult> ListAsync(int integrationId, GatewayDbContext db, CancellationToken ct)
    {
        var subscribers = await db.EmailSubscribers
            .Where(s => s.IntegrationId == integrationId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.ToDto())
            .ToListAsync(ct);

        return Results.Ok(subscribers);
    }

    private static async Task<IResult> AddAsync(
        int integrationId,
        [FromBody] AddSubscriberRequest req,
        GatewayDbContext db,
        NotificationStore notifications,
        IOptions<NotificationOptions> options,
        ClaimsPrincipal user,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || !MailAddress.TryCreate(req.Email.Trim(), out var parsed))
        {
            return Results.BadRequest(new ErrorResponse("El correo electrónico no es válido."));
        }

        var integration = await db.Integrations.FindAsync([integrationId], ct);
        if (integration is null)
        {
            return Results.NotFound(new ErrorResponse("La integración no existe."));
        }

        var email = parsed.Address.ToLowerInvariant();
        var exists = await db.EmailSubscribers.AnyAsync(s => s.IntegrationId == integrationId && s.Email == email, ct);
        if (exists)
        {
            return Results.Conflict(new ErrorResponse("Este correo ya está suscrito a esta integración."));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var verificationToken = Guid.NewGuid().ToString("N");
        var unsubscribeToken = Guid.NewGuid().ToString("N");
        var createdBy = user.Identity?.Name ?? "Admin";

        var subscriber = new IntegrationEmailSubscriber
        {
            IntegrationId = integrationId,
            Email = email,
            Status = SubscriberStatus.PendingVerification,
            VerificationToken = verificationToken,
            VerificationExpiresAt = now.AddHours(48),
            UnsubscribeToken = unsubscribeToken,
            CreatedAt = now,
            CreatedBy = createdBy
        };

        db.EmailSubscribers.Add(subscriber);
        await db.SaveChangesAsync(ct);

        var verifyUrl = $"{options.Value.ApiBaseUrl.TrimEnd('/')}/api/alerts/verify?token={verificationToken}";
        var subject = $"[Webhook Gateway] Confirma tu suscripción a alertas de {integration.Name}";
        var details = new IncidentDetails(
            IntegrationName: integration.Name,
            EndpointName: "Suscripción a Alertas",
            TargetUrl: verifyUrl,
            StatusCode: 200,
            ErrorSummary: "Por favor confirme haciendo clic en el enlace para activar la recepción de alertas.",
            AttemptCount: 1,
            OccurredAtUtc: now,
            DashboardUrl: verifyUrl);

        await notifications.EnqueueAsync(
            NotificationChannel.Email,
            email,
            integrationId,
            null,
            null,
            NotificationAlertType.Verification,
            subject,
            JsonSerializer.Serialize(details),
            ct);

        return Results.Created($"/api/integrations/{integrationId}/subscribers/{subscriber.Id}", subscriber.ToDto());
    }

    private static async Task<IResult> DeleteAsync(int integrationId, int subscriberId, GatewayDbContext db, CancellationToken ct)
    {
        var subscriber = await db.EmailSubscribers.FirstOrDefaultAsync(s => s.Id == subscriberId && s.IntegrationId == integrationId, ct);
        if (subscriber is null)
        {
            return Results.NotFound(new ErrorResponse("El suscriptor no existe."));
        }

        db.EmailSubscribers.Remove(subscriber);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ToggleMuteAsync(int integrationId, int subscriberId, GatewayDbContext db, CancellationToken ct)
    {
        var subscriber = await db.EmailSubscribers
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Id == subscriberId && s.IntegrationId == integrationId, ct);
        if (subscriber is null)
        {
            return Results.NotFound(new ErrorResponse("El suscriptor no existe."));
        }

        subscriber.Status = subscriber.Status == SubscriberStatus.Silenced
            ? SubscriberStatus.Verified
            : SubscriberStatus.Silenced;

        await db.SaveChangesAsync(ct);
        return Results.Ok(subscriber.ToDto());
    }

    private static async Task<IResult> ResendVerificationAsync(
        int integrationId, int subscriberId, GatewayDbContext db, NotificationStore notifications,
        IOptions<NotificationOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var subscriber = await db.EmailSubscribers
            .AsTracking()
            .Include(s => s.Integration)
            .FirstOrDefaultAsync(s => s.Id == subscriberId && s.IntegrationId == integrationId, ct);

        if (subscriber is null)
        {
            return Results.NotFound(new ErrorResponse("El suscriptor no existe."));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        subscriber.VerificationToken = Guid.NewGuid().ToString("N");
        subscriber.VerificationExpiresAt = now.AddHours(48);
        await db.SaveChangesAsync(ct);

        var verifyUrl = $"{options.Value.ApiBaseUrl.TrimEnd('/')}/api/alerts/verify?token={subscriber.VerificationToken}";
        var integrationName = subscriber.Integration?.Name ?? "Integración";
        var subject = $"[Webhook Gateway] Confirma tu suscripción a alertas de {integrationName}";
        var details = new IncidentDetails(
            IntegrationName: integrationName,
            EndpointName: "Suscripción a Alertas",
            TargetUrl: verifyUrl,
            StatusCode: 200,
            ErrorSummary: "Por favor confirme haciendo clic en el botón para activar la recepción de alertas.",
            AttemptCount: 1,
            OccurredAtUtc: now,
            DashboardUrl: verifyUrl);

        await notifications.EnqueueAsync(
            NotificationChannel.Email,
            subscriber.Email,
            integrationId,
            null,
            null,
            NotificationAlertType.Verification,
            subject,
            JsonSerializer.Serialize(details),
            ct);

        return Results.Ok(new { message = "Correo de verificación reenviado con éxito." });
    }
}
