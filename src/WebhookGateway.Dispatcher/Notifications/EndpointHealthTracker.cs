using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebhookGateway.Core.Domain;
using WebhookGateway.Core.Notifications;
using WebhookGateway.Data.Db;
using WebhookGateway.Dispatcher.Sending;

namespace WebhookGateway.Dispatcher.Notifications;

/// <summary>
/// Gestiona la máquina de estados de salud de cada endpoint de destino:
/// Healthy (0) -> Degraded (1) -> Down (2) -> Recovered (3).
/// Suprime spam disparando alertas exclusivamente durante las transiciones.
/// </summary>
public sealed class EndpointHealthTracker(
    NotificationStore notifications,
    ISqlConnectionFactory connectionFactory,
    TimeProvider clock,
    IOptions<NotificationOptions> options,
    ILogger<EndpointHealthTracker> logger)
{
    private const int DegradedThreshold = 3;

    private const string UpsertHealthSql = """
        MERGE dbo.EndpointHealthState AS target
        USING (SELECT @EndpointId AS OutboundEndpointId) AS src
        ON target.OutboundEndpointId = src.OutboundEndpointId
        WHEN MATCHED THEN
            UPDATE SET HealthStatus = @HealthStatus,
                       ConsecutiveFailures = @ConsecutiveFailures,
                       ConsecutiveSuccesses = @ConsecutiveSuccesses,
                       LastTransitionAt = @Now,
                       LastAlertSentAt = CASE WHEN @AlertSent = 1 THEN @Now ELSE target.LastAlertSentAt END,
                       LastErrorMessage = @LastErrorMessage,
                       LastStatusCode = @LastStatusCode
        WHEN NOT MATCHED THEN
            INSERT (OutboundEndpointId, HealthStatus, ConsecutiveFailures, ConsecutiveSuccesses,
                    LastTransitionAt, LastAlertSentAt, LastErrorMessage, LastStatusCode)
            VALUES (@EndpointId, @HealthStatus, @ConsecutiveFailures, @ConsecutiveSuccesses,
                    @Now, CASE WHEN @AlertSent = 1 THEN @Now ELSE NULL END, @LastErrorMessage, @LastStatusCode);
        """;

    private readonly ConcurrentDictionary<int, EndpointHealthStateMemory> _states = new();
    private readonly NotificationOptions _options = options.Value;

    public async Task RecordAttemptAsync(
        OutboundTarget target,
        AttemptVerdict verdict,
        short? statusCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var downThreshold = target.BreakerFailureThreshold;

        var existing = _states.GetOrAdd(target.Id, _ => new EndpointHealthStateMemory(EndpointHealthStatus.Healthy, 0, 0));

        var (newStatus, newFailures, newSuccesses, alertType) = EvaluateNextState(
            existing.Status, existing.ConsecutiveFailures, existing.ConsecutiveSuccesses, verdict, downThreshold);

        _states[target.Id] = new EndpointHealthStateMemory(newStatus, newFailures, newSuccesses);

        var transitionOccurred = newStatus != existing.Status;

        if (transitionOccurred)
        {
            logger.LogInformation(
                "Transición de salud en Endpoint {EndpointId} ({EndpointName}): {OldStatus} -> {NewStatus}",
                target.Id, target.EndpointName, existing.Status, newStatus);

            using var connection = await connectionFactory.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                UpsertHealthSql,
                new
                {
                    EndpointId = target.Id,
                    HealthStatus = (byte)newStatus,
                    ConsecutiveFailures = newFailures,
                    ConsecutiveSuccesses = newSuccesses,
                    Now = now,
                    AlertSent = alertType.HasValue ? 1 : 0,
                    LastErrorMessage = errorMessage,
                    LastStatusCode = statusCode
                },
                cancellationToken: cancellationToken));

            if (alertType.HasValue && _options.Enabled && !string.IsNullOrWhiteSpace(_options.AdminEmail))
            {
                await EnqueueHealthAlertAsync(target, alertType.Value, newFailures, statusCode, errorMessage, now, cancellationToken);
            }
        }
    }

    public static (EndpointHealthStatus Status, int Failures, int Successes, NotificationAlertType? Alert) EvaluateNextState(
        EndpointHealthStatus currentStatus, int currentFailures, int currentSuccesses, AttemptVerdict verdict, int downThreshold)
    {
        if (verdict == AttemptVerdict.Retryable)
        {
            var failures = currentFailures + 1;

            if (failures >= downThreshold)
            {
                var alert = currentStatus != EndpointHealthStatus.Down ? NotificationAlertType.EndpointDown : (NotificationAlertType?)null;
                return (EndpointHealthStatus.Down, failures, 0, alert);
            }

            if (failures >= DegradedThreshold && currentStatus == EndpointHealthStatus.Healthy)
            {
                return (EndpointHealthStatus.Degraded, failures, 0, NotificationAlertType.EndpointDegraded);
            }

            return (currentStatus, failures, 0, null);
        }

        if (verdict == AttemptVerdict.Success)
        {
            var successes = currentSuccesses + 1;

            if (currentStatus is EndpointHealthStatus.Down or EndpointHealthStatus.Degraded)
            {
                return (EndpointHealthStatus.Recovered, 0, successes, NotificationAlertType.EndpointRecovered);
            }

            if (currentStatus == EndpointHealthStatus.Recovered && successes >= 2)
            {
                return (EndpointHealthStatus.Healthy, 0, successes, null);
            }

            return (currentStatus, 0, successes, null);
        }

        // Permanent failure (ej: 400 Bad Request): no cambia la salud del host
        return (currentStatus, currentFailures, currentSuccesses, null);
    }

    private async Task EnqueueHealthAlertAsync(
        OutboundTarget target,
        NotificationAlertType alertType,
        int attemptCount,
        short? statusCode,
        string? errorMessage,
        DateTime occurredAt,
        CancellationToken cancellationToken)
    {
        var (prefix, summary) = alertType switch
        {
            NotificationAlertType.EndpointDown => ("[URGENTE] Endpoint Caído", "El endpoint alcanzó el límite de fallos y su circuito está abierto. Entregas pausadas."),
            NotificationAlertType.EndpointDegraded => ("[ALERTA] Endpoint Degradado", "El endpoint ha presentado fallos consecutivos intermitentes."),
            NotificationAlertType.EndpointRecovered => ("[RESOLUCIÓN] Endpoint Recuperado", "El endpoint ha vuelto a responder exitosamente (2xx). Tráfico reanudado."),
            _ => ("[ALERTA] Estado de Endpoint", "Cambio en la salud del endpoint.")
        };

        var subject = $"{prefix}: {target.IntegrationName} / {target.EndpointName}";
        var details = new IncidentDetails(
            IntegrationName: target.IntegrationName,
            EndpointName: target.EndpointName,
            TargetUrl: target.TargetUrl.ToString(),
            StatusCode: statusCode,
            ErrorSummary: errorMessage ?? summary,
            AttemptCount: (short)attemptCount,
            OccurredAtUtc: occurredAt,
            DashboardUrl: $"{_options.DashboardBaseUrl}/endpoints/{target.Id}");

        var metadataJson = JsonSerializer.Serialize(details);

        var subscribers = await notifications.GetVerifiedSubscriberEmailsAsync(target.IntegrationId, cancellationToken);
        var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(_options.AdminEmail))
        {
            recipients.Add(_options.AdminEmail);
        }

        foreach (var email in subscribers)
        {
            recipients.Add(email);
        }

        foreach (var recipient in recipients)
        {
            await notifications.EnqueueAsync(
                NotificationChannel.Email, recipient, target.IntegrationId,
                target.Id, null, alertType, subject, metadataJson, cancellationToken);
        }
    }

    private sealed record EndpointHealthStateMemory(
        EndpointHealthStatus Status, int ConsecutiveFailures, int ConsecutiveSuccesses);
}

