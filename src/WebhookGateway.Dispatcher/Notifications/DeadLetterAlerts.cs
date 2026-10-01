using System.Text.Json;
using Microsoft.Extensions.Logging;
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
    DeadLetterWindows windows,
    TimeProvider clock,
    IOptions<NotificationOptions> options,
    ILogger<DeadLetterAlerts> logger)
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

        /*
            Un aviso por destino cada X minutos. Un destino que responde 400 a todo descarta una
            entrega por mensaje, y sin esto cada una era un correo a cada destinatario: con 2.000
            mensajes en un pico, miles de correos que nadie lee y que además ahogan los avisos de los
            destinos que sí importan.
        */
        var calladas = windows.TryClaimAlert(
            target.Id, TimeSpan.FromMinutes(options.Value.DeadLetterGroupingMinutes));

        if (calladas is null)
        {
            logger.LogDebug(
                "Entrega {DeliveryId} descartada en el destino {EndpointId}: no se avisa, ya se avisó hace poco.",
                delivery.Id, target.Id);
            return;
        }

        // El aviso dice cuántas más hubo mientras callaba, que es lo que de verdad hace falta saber.
        var sufijo = calladas > 0
            ? $" (y {calladas.Value} más desde el último aviso)"
            : string.Empty;

        var subject = $"[ALERTA] Entrega descartada{sufijo}: {target.IntegrationName} / {target.EndpointName}";

        var motivo = result.ErrorMessage ?? "Intentos de entrega agotados sin éxito.";

        if (calladas > 0)
        {
            motivo += $" Otras {calladas.Value} entregas de este destino se descartaron desde el aviso anterior.";
        }

        var details = new IncidentDetails(
            IntegrationName: target.IntegrationName,
            EndpointName: target.EndpointName,
            TargetUrl: target.TargetUrl.ToString(),
            StatusCode: (short?)result.StatusCode,
            ErrorSummary: motivo,
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
