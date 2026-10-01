using Microsoft.EntityFrameworkCore;
using WebhookGateway.Core.Notifications;
using WebhookGateway.Data;

namespace WebhookGateway.Api.Panel;

public static class AlertActionEndpoints
{
    public static void MapAlertActions(this WebApplication app)
    {
        app.MapGet("/api/alerts/verify", VerifyEmailAsync).AllowAnonymous();
        app.MapGet("/api/alerts/unsubscribe", UnsubscribeAsync).AllowAnonymous();
        app.MapPost("/api/alerts/unsubscribe", UnsubscribeAsync).AllowAnonymous();
    }

    private static async Task<IResult> VerifyEmailAsync(
        string? token, HttpRequest req, GatewayDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var wantsJson = req.Headers.Accept.ToString().Contains("application/json");

        if (string.IsNullOrWhiteSpace(token))
        {
            return wantsJson
                ? Results.BadRequest(new ErrorResponse("Token inválido o expirado."))
                : Results.Content(AlertHtmlRenderer.Render("Enlace no válido", "El enlace de verificación no contiene un token válido.", AlertHtmlType.Error), "text/html");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var sub = await db.EmailSubscribers
            .AsTracking()
            .FirstOrDefaultAsync(s => s.VerificationToken == token && s.VerificationExpiresAt > now, ct);
        if (sub is null)
        {
            return wantsJson
                ? Results.NotFound(new ErrorResponse("El enlace de verificación es inválido o ya ha expirado."))
                : Results.Content(AlertHtmlRenderer.Render("Enlace no válido o expirado", "El enlace de verificación es inválido o ya ha expirado.", AlertHtmlType.Error, "Los enlaces de verificación expiran a las 48 horas. Solicita al administrador de la integración que te reenvíe la confirmación."), "text/html");
        }

        sub.Status = SubscriberStatus.Verified;
        sub.VerifiedAt = now;
        sub.VerificationToken = null;
        sub.VerificationExpiresAt = null;
        await db.SaveChangesAsync(ct);

        return wantsJson
            ? Results.Ok(new { message = "¡Correo verificado con éxito! Tu suscripción a alertas está activa." })
            : Results.Content(AlertHtmlRenderer.Render("¡Suscripción Confirmada!", "Tu correo ha sido verificado con éxito. A partir de este momento recibirás las alertas e incidentes de Webhook Gateway.", AlertHtmlType.Success), "text/html");
    }

    private static async Task<IResult> UnsubscribeAsync(
        string? token, HttpRequest req, GatewayDbContext db, CancellationToken ct)
    {
        var wantsJson = req.Headers.Accept.ToString().Contains("application/json");

        if (string.IsNullOrWhiteSpace(token))
        {
            return wantsJson
                ? Results.BadRequest(new ErrorResponse("Token de baja inválido."))
                : Results.Content(AlertHtmlRenderer.Render("Enlace no válido", "El enlace de baja no contiene un token válido.", AlertHtmlType.Error), "text/html");
        }

        var sub = await db.EmailSubscribers
            .AsTracking()
            .FirstOrDefaultAsync(s => s.UnsubscribeToken == token, ct);
        if (sub is null)
        {
            return wantsJson
                ? Results.NotFound(new ErrorResponse("No se encontró la suscripción asociada a este enlace."))
                : Results.Content(AlertHtmlRenderer.Render("Suscripción no encontrada", "No se encontró una suscripción activa asociada a este enlace.", AlertHtmlType.Error), "text/html");
        }

        sub.Status = SubscriberStatus.Unsubscribed;
        await db.SaveChangesAsync(ct);

        return wantsJson
            ? Results.Ok(new { message = "Te has dado de baja exitosamente de las alertas." })
            : Results.Content(AlertHtmlRenderer.Render("Te has dado de baja", "Has cancelado exitosamente la recepción de alertas de esta integración.", AlertHtmlType.Info), "text/html");
    }
}
