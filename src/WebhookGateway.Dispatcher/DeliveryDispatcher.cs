using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using WebhookGateway.Core.Delivery;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Traffic;
using WebhookGateway.Dispatcher.Claiming;
using WebhookGateway.Dispatcher.Recording;
using WebhookGateway.Dispatcher.Sending;
using WebhookGateway.Dispatcher.Throttling;

namespace WebhookGateway.Dispatcher;

/// <summary>
/// Entrega una reclamación: resuelve el destino, respeta su ritmo, envía y decide qué pasa
/// después. Es donde se junta todo lo demás.
/// </summary>
public sealed class DeliveryDispatcher(
    OutboundTargetCache targets,
    MessagePayloadReader payloads,
    DeliverySender sender,
    EndpointThrottles throttles,
    EndpointBreakers breakers,
    DeliveryRecorder recorder,
    TimeProvider clock,
    IOptions<DispatcherOptions> options,
    ILogger<DeliveryDispatcher> logger)
{
    private readonly string _workerId = options.Value.WorkerId;

    public async Task DispatchAsync(ClaimedDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        var now = clock.GetUtcNow().UtcDateTime;
        var target = await targets.GetAsync(delivery.OutboundEndpointId, cancellationToken);

        if (target is null)
        {
            Terminate(delivery, DeliveryStatus.Failed, now, "El destino ya no existe o está desactivado.");
            return;
        }

        // Circuito abierto: el destino está caído y no vamos a gastar un intento —ni la
        // ventana de entrega— confirmándolo.
        if (breakers.OpenUntil(target.Id) is { } openUntil)
        {
            Reschedule(delivery, openUntil.UtcDateTime, "El circuito del destino está abierto.");
            return;
        }

        var payload = await payloads.LoadAsync(delivery.MessageId, delivery.CreatedAt, cancellationToken);

        if (payload is null)
        {
            Terminate(delivery, DeliveryStatus.Failed, now, "El cuerpo del mensaje ya no está disponible: se purgó antes de entregarlo.");
            return;
        }

        using var lease = await throttles.AcquireAsync(target, cancellationToken);

        if (!lease.IsAcquired)
        {
            // Hay más entregas esperando de las que este destino puede absorber. Vuelve a la
            // cola en breve en vez de quedarse ocupando un hilo.
            Reschedule(delivery, now.AddSeconds(5), null);
            return;
        }

        var attemptNumber = (short)(delivery.AttemptCount + 1);
        var startedAt = clock.GetUtcNow().UtcDateTime;
        var stopwatch = Stopwatch.StartNew();

        var result = await sender.SendAsync(target, payload, cancellationToken);

        stopwatch.Stop();

        var verdict = AttemptClassifier.Classify(result.StatusCode);
        breakers.Record(target.Id, verdict, target.BreakerFailureThreshold, target.BreakerOpenSeconds);

        var attempt = new AttemptRecord(
            delivery.Id, startedAt, attemptNumber, (int)stopwatch.ElapsedMilliseconds,
            (short?)result.StatusCode, result.ResponseHeadersJson, result.ResponseBody, result.ErrorMessage, _workerId);

        recorder.Add(attempt, Decide(delivery, target, result, verdict, attemptNumber));
    }

    /// <summary>Traduce el veredicto del intento al nuevo estado de la entrega.</summary>
    private DeliveryUpdate Decide(
        ClaimedDelivery delivery, OutboundTarget target, SendResult result, AttemptVerdict verdict, short attemptNumber)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lastError = result.ErrorMessage;

        if (verdict == AttemptVerdict.Success)
        {
            return Update(delivery, DeliveryStatus.Delivered, attemptNumber, now, result.StatusCode, null, now);
        }

        if (verdict == AttemptVerdict.Permanent)
        {
            logger.LogInformation(
                "Entrega {DeliveryId} descartada: el destino {EndpointId} respondió {StatusCode} y eso no se arregla insistiendo.",
                delivery.Id, target.Id, result.StatusCode);

            return Update(delivery, DeliveryStatus.Failed, attemptNumber, now, result.StatusCode, lastError, now);
        }

        var delay = target.RetryPolicy.NextDelay(attemptNumber, result.RetryAfter);

        if (delay is null)
        {
            return Update(delivery, DeliveryStatus.Failed, attemptNumber, now, result.StatusCode,
                lastError ?? $"Se agotaron los {target.RetryPolicy.MaxAttempts} intentos.", now);
        }

        if (!RetryPolicy.FitsInWindow(now, delay.Value, delivery.ExpiresAt))
        {
            return Update(delivery, DeliveryStatus.Expired, attemptNumber, now, result.StatusCode,
                lastError ?? "El siguiente reintento caía fuera de la ventana de entrega.", now);
        }

        return Update(delivery, DeliveryStatus.Retrying, attemptNumber, now + delay.Value, result.StatusCode, lastError, null);
    }

    /// <summary>Cierra la entrega sin haber llegado a hacer una petición, así que no hay intento que registrar.</summary>
    private void Terminate(ClaimedDelivery delivery, DeliveryStatus status, DateTime now, string reason)
    {
        logger.LogWarning("Entrega {DeliveryId} cerrada sin intentarla: {Reason}", delivery.Id, reason);

        recorder.Add(null, Update(delivery, status, delivery.AttemptCount, now, null, reason, now));
    }

    /// <summary>Devuelve la entrega a la cola sin consumir intento: no la hemos enviado.</summary>
    private void Reschedule(ClaimedDelivery delivery, DateTime nextAttemptAt, string? reason) =>
        recorder.Add(null, Update(delivery, DeliveryStatus.Retrying, delivery.AttemptCount, nextAttemptAt, null, reason, null));

    private static DeliveryUpdate Update(
        ClaimedDelivery delivery, DeliveryStatus status, short attemptCount,
        DateTime nextAttemptAt, int? statusCode, string? lastError, DateTime? completedAt) =>
        new(delivery.Id, delivery.CreatedAt, (byte)status, attemptCount, nextAttemptAt, (short?)statusCode, lastError, completedAt);
}
