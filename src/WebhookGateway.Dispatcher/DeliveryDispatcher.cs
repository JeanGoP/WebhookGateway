using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebhookGateway.Core.Delivery;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Traffic;
using WebhookGateway.Dispatcher.Claiming;
using WebhookGateway.Dispatcher.Notifications;
using WebhookGateway.Dispatcher.Recording;
using WebhookGateway.Dispatcher.Sending;
using WebhookGateway.Dispatcher.Throttling;

namespace WebhookGateway.Dispatcher;

/// <summary>
/// Entrega una reclamación: envía y decide qué pasa después. Es donde se junta todo lo demás.
/// </summary>
/// <remarks>
/// Lo que aquí <em>no</em> se hace es esperar turno en el limitador del destino. Esa espera la hace
/// quien llama, antes de ocupar un hueco de capacidad global, y por una razón concreta: cuando se
/// esperaba aquí dentro, una entrega a un destino limitado a 60/min se quedaba quieta quince
/// segundos ocupando uno de los pocos huecos de paralelismo, y los destinos sanos no avanzaban. El
/// contrato de <see cref="DispatchAsync"/> es que el turno ya está concedido.
/// </remarks>
public sealed class DeliveryDispatcher(
    MessagePayloadReader payloads,
    DeliverySender sender,
    EndpointBreakers breakers,
    DeliveryRecorder recorder,
    EndpointHealthTracker healthTracker,
    DeadLetterAlerts deadLetters,
    TimeProvider clock,
    IOptions<DispatcherOptions> options,
    ILogger<DeliveryDispatcher> logger)
{
    private readonly string _workerId = options.Value.WorkerId;

    public Task<Dictionary<long, OutgoingPayload>> PreloadPayloadsAsync(IEnumerable<long> messageIds, CancellationToken ct) =>
        payloads.LoadBatchAsync(messageIds, ct);

    /// <summary>
    /// Envía una entrega a su destino. Quien llama ya resolvió el destino y ya tiene su turno.
    /// </summary>
    public async Task DispatchAsync(
        ClaimedDelivery delivery, OutboundTarget target, OutgoingPayload? payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(target);

        var now = clock.GetUtcNow().UtcDateTime;

        /*
            El circuito se comprueba también aquí, no solo antes de reclamar: puede abrirse a mitad
            de un lote, con los fallos que acaba de dar este mismo destino. Entonces lo que queda
            del lote se reprograma en vez de seguir insistiendo.
        */
        if (breakers.OpenUntil(target.Id) is { } openUntil)
        {
            Reschedule(delivery, openUntil.UtcDateTime, "El circuito del destino está abierto.");
            return;
        }

        payload ??= await payloads.LoadAsync(delivery.MessageId, cancellationToken);

        if (payload is null)
        {
            Terminate(delivery, DeliveryStatus.Failed, now, "El cuerpo del mensaje ya no está disponible: se purgó antes de entregarlo.");
            return;
        }

        var attemptNumber = (short)(delivery.AttemptCount + 1);
        var startedAt = clock.GetUtcNow().UtcDateTime;
        var stopwatch = Stopwatch.StartNew();

        var result = await sender.SendAsync(target, payload, cancellationToken);

        stopwatch.Stop();

        var verdict = AttemptClassifier.Classify(result.StatusCode);
        breakers.Record(target.Id, verdict, target.BreakerFailureThreshold, target.BreakerOpenSeconds);
        await healthTracker.RecordAttemptAsync(target, verdict, (short?)result.StatusCode, result.ErrorMessage, cancellationToken);

        var attempt = new AttemptRecord(
            delivery.Id, startedAt, attemptNumber, (int)stopwatch.ElapsedMilliseconds,
            (short?)result.StatusCode, result.ResponseHeadersJson, result.ResponseBody, result.ErrorMessage, _workerId);

        var update = Decide(delivery, target, result, verdict, attemptNumber);
        recorder.Add(attempt, update);

        var descartada = update.Status == (byte)DeliveryStatus.Failed
                      || update.Status == (byte)DeliveryStatus.Expired;

        if (descartada && deadLetters.Enabled)
        {
            await deadLetters.RaiseAsync(delivery, target, result, attemptNumber, cancellationToken);
        }
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

    /// <summary>
    /// El destino ya no existe o lo desactivaron: la entrega no se reintenta, porque no hay a dónde
    /// mandarla.
    /// </summary>
    public void TerminateMissingTarget(ClaimedDelivery delivery) =>
        Terminate(delivery, DeliveryStatus.Failed, clock.GetUtcNow().UtcDateTime,
            "El destino ya no existe o está desactivado.");

    /// <summary>
    /// El destino tiene más entregas esperando de las que su limitador puede absorber. Vuelve a la
    /// cola en breve sin consumir intento: no la hemos enviado.
    /// </summary>
    public void RescheduleWithoutTurn(ClaimedDelivery delivery) =>
        Reschedule(delivery, clock.GetUtcNow().UtcDateTime.AddSeconds(5),
            "El destino no tenía turno libre en su limitador de ritmo.");

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
