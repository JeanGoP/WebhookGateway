using System.Text.Json;
using Microsoft.Extensions.Options;
using WebhookGateway.Core.Notifications;
using WebhookGateway.Dispatcher.Claiming;
using WebhookGateway.Dispatcher.Sending;

namespace WebhookGateway.Dispatcher.Notifications;

/// <summary>
/// El aviso de que una entrega se ha descartado: encola un correo para los suscriptores
/// verificados de la integración y para el administrador.
/// </summary>
/// <remarks>
/// Encola, no envía. Quien manda es <see cref="NotificationWorker"/>, drenando la bandeja de
/// salida: así una caída del SMTP no frena ni una sola entrega.
/// </remarks>
public sealed class DeadLetterAlerts(
    NotificationStore notifications,
    TimeProvider clock,
    IOptions<NotificationOptions> options)
{
    /// <summary>Si no hay a quién avisar, no se construye el aviso.</summary>
    public bool Enabled =>
        options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.AdminEmail);

    public async Task RaiseAsync(
        ClaimedDelivery delivery,
        OutboundTarget target,
        SendResult result,
        short attemptNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(result);

        var subject = $"[ALERTA] Entrega descartada: {target.IntegrationName} / {target.EndpointName}";

        var details = new IncidentDetails(
            IntegrationName: target.IntegrationName,
            EndpointName: target.EndpointName,
            TargetUrl: target.TargetUrl.ToString(),
            StatusCode: (short?)result.StatusCode,
            ErrorSummary: result.ErrorMessage ?? "Intentos de entrega agotados sin éxito.",
            AttemptCount: attemptNumber,
            OccurredAtUtc: clock.GetUtcNow().UtcDateTime,
            DashboardUrl: $"{options.Value.DashboardBaseUrl}/deliveries/{delivery.Id}");

        var metadataJson = JsonSerializer.Serialize(details);

        var subscribers = await notifications.GetVerifiedSubscriberEmailsAsync(target.IntegrationId, cancellationToken);
        var recipients = new HashSet<string>(subscribers, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(options.Value.AdminEmail))
        {
            recipients.Add(options.Value.AdminEmail);
        }

        foreach (var recipient in recipients)
        {
            await notifications.EnqueueAsync(
                NotificationChannel.Email, recipient, target.IntegrationId, target.Id,
                delivery.Id, NotificationAlertType.DeadLetter, subject, metadataJson, cancellationToken);
        }
    }
}
