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
/// <remarks>
/// Un destino tiene decenas de entregas en vuelo a la vez, y cuando cae o vuelve todas lo notan
/// casi al mismo tiempo. Antes cada una leía el estado, calculaba el siguiente y lo escribía sin
/// coordinarse: varias detectaban la misma transición, cada una lanzaba su MERGE, y dos MERGE sin
/// HOLDLOCK que ven "no existe" insertan los dos y uno revienta con la clave primaria. Esa
/// excepción cortaba la entrega después del HTTP y antes de apuntar su resultado, que se reenviaba
/// al vencer el lease: un duplicado. Se vio en la prueba de carga del 2026-10-05.
/// <para>
/// Ahora cada destino tiene su cerrojo: evaluar, escribir y avisar van en orden, de uno en uno, y
/// la base recibe las transiciones en el mismo orden en que ocurrieron. Las transiciones son raras,
/// así que esperar el MERGE dentro del cerrojo no cuesta nada que se note. HOLDLOCK queda para dos
/// instancias vivas, que no comparten memoria.
/// </para>
/// </remarks>
public sealed class EndpointHealthTracker(
    NotificationStore notifications,
    ISqlConnectionFactory connectionFactory,
    TimeProvider clock,
    IOptions<NotificationOptions> options,
    ILogger<EndpointHealthTracker> logger)
{
    private const string UpsertHealthSql = """
        MERGE dbo.EndpointHealthState WITH (HOLDLOCK) AS target
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

    private readonly ConcurrentDictionary<int, HealthCell> _cells = new();
    private readonly NotificationOptions _options = options.Value;

    public async Task RecordAttemptAsync(
        OutboundTarget target,
        AttemptVerdict verdict,
        short? statusCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var cell = _cells.GetOrAdd(target.Id, _ => new HealthCell());

        await cell.Gate.WaitAsync(cancellationToken);

        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var previous = cell.Status;

            var (newStatus, newFailures, newSuccesses, alertType) = EndpointHealthRules.EvaluateNextState(
                cell.Status, cell.ConsecutiveFailures, cell.ConsecutiveSuccesses, verdict, target.BreakerFailureThreshold);

            (cell.Status, cell.ConsecutiveFailures, cell.ConsecutiveSuccesses) = (newStatus, newFailures, newSuccesses);

            if (newStatus == previous)
            {
                return;
            }

            logger.LogInformation(
                "Transición de salud en Endpoint {EndpointId} ({EndpointName}): {OldStatus} -> {NewStatus}",
                target.Id, target.EndpointName, previous, newStatus);

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
        finally
        {
            cell.Gate.Release();
        }
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

    /// <summary>El estado en memoria de un destino y el cerrojo que lo protege.</summary>
    private sealed class HealthCell
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public EndpointHealthStatus Status { get; set; } = EndpointHealthStatus.Healthy;

        public int ConsecutiveFailures { get; set; }

        public int ConsecutiveSuccesses { get; set; }
    }
}

