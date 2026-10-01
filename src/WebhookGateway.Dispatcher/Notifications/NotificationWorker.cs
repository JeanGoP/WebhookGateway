using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebhookGateway.Core.Notifications;

namespace WebhookGateway.Dispatcher.Notifications;

/// <summary>
/// Worker en segundo plano que drena la NotificationOutbox y despacha alertas sin bloquear el flujo principal.
/// </summary>
public sealed class NotificationWorker(
    NotificationStore store,
    SmtpNotificationSender sender,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"notif-{Environment.MachineName}-{Environment.ProcessId}";
    private readonly NotificationOptions _options = options.Value;
    private DateTime _lastLeaseRecovery = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Subsistema de notificaciones desactivado por configuración (Gateway:Notifications:Enabled = false).");
            return;
        }

        logger.LogInformation("NotificationWorker iniciado. WorkerId: {WorkerId}", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = clock.GetUtcNow().UtcDateTime;

                // Recuperar leases huérfanos cada 60 segundos
                if (now - _lastLeaseRecovery > TimeSpan.FromSeconds(60))
                {
                    _lastLeaseRecovery = now;
                    var recovered = await store.RecoverOrphanedLeasesAsync(now, stoppingToken);
                    if (recovered > 0)
                    {
                        logger.LogInformation("Se recuperaron {Count} notificaciones con leases huérfanos.", recovered);
                    }
                }

                var leaseUntil = now.AddSeconds(_options.Drainer.LeaseSeconds);
                var batch = await store.ClaimAsync(now, leaseUntil, _workerId, _options.Drainer.BatchSize, stoppingToken);

                if (batch.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.Drainer.PollIntervalSeconds), clock, stoppingToken);
                    continue;
                }

                logger.LogDebug("Procesando lote de {Count} notificaciones.", batch.Count);

                foreach (var item in batch)
                {
                    await ProcessNotificationAsync(item, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // La regla de oro: fallos del worker jamás generan más alertas, solo se loguean
                logger.LogError(ex, "Error imprevisto en el ciclo del NotificationWorker.");
                await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
            }
        }

        logger.LogInformation("NotificationWorker detenido ordenadamente.");
    }

    private async Task ProcessNotificationAsync(ClaimedNotification item, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        try
        {
            IncidentDetails? details = null;
            if (!string.IsNullOrWhiteSpace(item.MetadataJson))
            {
                details = JsonSerializer.Deserialize<IncidentDetails>(item.MetadataJson);
            }

            details ??= new IncidentDetails(
                IntegrationName: "Desconocida",
                EndpointName: "Desconocido",
                TargetUrl: "N/A",
                StatusCode: null,
                ErrorSummary: item.Subject,
                AttemptCount: item.AttemptCount,
                OccurredAtUtc: now,
                DashboardUrl: _options.DashboardBaseUrl);

            var (html, text) = (NotificationAlertType)item.AlertType == NotificationAlertType.Verification
                ? EmailTemplateBuilder.BuildVerificationEmail(item.Subject, details.IntegrationName, details.DashboardUrl)
                : EmailTemplateBuilder.BuildIncidentEmail(item.Subject, (NotificationAlertType)item.AlertType, details);

            var result = await sender.SendEmailAsync(item.Recipient, item.Subject, html, text, cancellationToken);

            if (result.Success)
            {
                await store.MarkDeliveredAsync(item.Id, result.MessageId, cancellationToken);
            }
            else
            {
                var nextAttempt = item.AttemptCount + 1;
                var isRetryable = result.IsRetryable && nextAttempt < _options.Drainer.MaxDeliveryAttempts;

                // Backoff exponencial para reintentos de correo (1m, 2m, 4m, 8m...)
                var delayMinutes = Math.Min(30, (int)Math.Pow(2, item.AttemptCount));
                var nextAttemptAt = now.AddMinutes(delayMinutes);

                await store.MarkFailedAsync(
                    item.Id,
                    result.ErrorMessage ?? "Fallo de envío SMTP",
                    isRetryable,
                    nextAttemptAt,
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Excepción procesando notificación {NotificationId} para {Recipient}", item.Id, item.Recipient);
            await store.MarkFailedAsync(item.Id, ex.Message, isRetryable: false, now, cancellationToken);
        }
    }
}
