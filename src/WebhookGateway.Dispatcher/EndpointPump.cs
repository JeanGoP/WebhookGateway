using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebhookGateway.Data.Traffic;
using WebhookGateway.Dispatcher.Claiming;
using WebhookGateway.Dispatcher.Recording;
using WebhookGateway.Dispatcher.Sending;
using WebhookGateway.Dispatcher.Throttling;

namespace WebhookGateway.Dispatcher;

/// <summary>
/// El avance de un solo destino: reclama lo suyo, lo envía a su ritmo y repite hasta vaciar su
/// cola. Cada destino tiene el suyo y ninguno espera a los demás.
/// </summary>
/// <remarks>
/// Sin lotes con barrera, ni entre destinos ni dentro de uno. Esperar a la entrega más lenta de un
/// lote es lo que hundía al ciclo antiguo a 3–7/s, y dentro de un destino lo dejaba en 80–160/s con
/// 100 ms de latencia y sin pasar de veinte huecos aunque tuviera treinta y dos (prueba de carga del
/// 2026-10-05). Ahora la concurrencia del destino se mantiene llena: cuando se libera una cuarta
/// parte, se vuelca lo terminado y se reclama lo que cabe. El techo es concurrencia ÷ latencia.
/// </remarks>
public sealed class EndpointPump(
    DeliveryClaimer claimer,
    DeliveryDispatcher dispatcher,
    DeliveryRecorder recorder,
    OutboundTargetCache targets,
    EndpointThrottles throttles,
    EndpointBreakers breakers,
    TimeProvider clock,
    IOptions<DispatcherOptions> options,
    ILogger<EndpointPump> logger)
{
    private readonly DispatcherOptions _options = options.Value;

    /// <summary>
    /// Vacía la cola de un destino. Vuelve cuando no queda nada que reclamar, cuando el circuito
    /// del destino está abierto o cuando se pide el apagado; en todos los casos, con lo que estaba
    /// en vuelo ya terminado.
    /// </summary>
    public async Task DrainAsync(int endpointId, SemaphoreSlim capacity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capacity);

        var inFlight = new List<Task>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                /*
                    Circuito abierto: no se reclama ni una fila. Antes esto se comprobaba por entrega,
                    ya reclamada, así que un destino caído con mil pendientes eran mil reclamaciones y
                    mil reprogramaciones en SQL para no enviar nada.
                */
                if (breakers.OpenUntil(endpointId) is not null)
                {
                    await FinishAsync(inFlight, cancellationToken);
                    return;
                }

                // El destino va antes que el claim: de su ritmo y su timeout sale cuántas reclamar.
                var target = await targets.GetAsync(endpointId, cancellationToken);

                if (target is null)
                {
                    await FinishAsync(inFlight, cancellationToken);
                    await TerminateMissingTargetAsync(endpointId, cancellationToken);
                    return;
                }

                var slots = Math.Max(1, target.MaxConcurrency);
                await WaitForRoomAsync(inFlight, slots);

                // Lo terminado se vuelca antes de pedir más: un resultado no espera más de un ciclo.
                await recorder.FlushAsync(cancellationToken);

                var batchSize = Math.Min(
                    slots - inFlight.Count,
                    ClaimSizing.For(target, _options.MaxPerEndpointPerClaim, _options.LeaseSeconds));

                var claimed = await ClaimAsync(endpointId, batchSize, cancellationToken);

                if (claimed.Count == 0)
                {
                    if (inFlight.Count == 0)
                    {
                        return;
                    }

                    // Nada vencido ahora mismo. Se termina lo que está en vuelo y se vuelve a mirar:
                    // mientras tanto puede haber llegado más, o vencido un reintento.
                    await FinishAsync(inFlight, cancellationToken);
                    continue;
                }

                var payloads = await dispatcher.PreloadPayloadsAsync(claimed.Select(d => d.MessageId), cancellationToken);

                foreach (var delivery in claimed)
                {
                    inFlight.Add(SendOneAsync(delivery, target, payloads, capacity, cancellationToken).AsTask());
                }
            }
        }
        finally
        {
            // Ni un fallo ni el apagado dejan envíos huérfanos. Sus resultados quedan en el recorder
            // y los vuelca el siguiente ciclo, o el apagado ordenado del supervisor.
            await Task.WhenAll(inFlight);
        }
    }

    /// <summary>
    /// Espera a que se libere al menos una cuarta parte de la concurrencia del destino. Reclamar por
    /// cada hueco suelto serían tres viajes a SQL por entrega.
    /// </summary>
    private static async Task WaitForRoomAsync(List<Task> inFlight, int slots)
    {
        inFlight.RemoveAll(t => t.IsCompleted);
        var refill = Math.Max(1, slots / 4);

        while (inFlight.Count > 0 && slots - inFlight.Count < refill)
        {
            await Task.WhenAny(inFlight);
            inFlight.RemoveAll(t => t.IsCompleted);
        }
    }

    private async Task FinishAsync(List<Task> inFlight, CancellationToken cancellationToken)
    {
        await Task.WhenAll(inFlight);
        inFlight.Clear();
        await recorder.FlushAsync(cancellationToken);
    }

    private Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(int endpointId, int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        return claimer.ClaimForEndpointAsync(
            endpointId, now, now.AddSeconds(_options.LeaseSeconds), _options.WorkerId, batchSize, cancellationToken);
    }

    /// <summary>El destino ya no existe o lo desactivaron: no hay a dónde mandar lo que tenga.</summary>
    private async Task TerminateMissingTargetAsync(int endpointId, CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(endpointId, _options.MaxPerEndpointPerClaim, cancellationToken);

        foreach (var delivery in claimed)
        {
            dispatcher.TerminateMissingTarget(delivery);
        }

        await recorder.FlushAsync(cancellationToken);
    }

    private async ValueTask SendOneAsync(
        ClaimedDelivery delivery,
        OutboundTarget target,
        Dictionary<long, OutgoingPayload> payloads,
        SemaphoreSlim capacity,
        CancellationToken cancellationToken)
    {
        try
        {
            // 1. El turno del destino. Esperar aquí solo retrasa a este destino, que es el que tiene
            //    el límite; los demás siguen a lo suyo en sus propias bombas.
            using var turn = await throttles.AcquireAsync(target, cancellationToken);

            if (!turn.IsAcquired)
            {
                dispatcher.RescheduleWithoutTurn(delivery);
                return;
            }

            // 2. La capacidad global, y DESPUÉS del turno: pedida antes, una entrega esperando el
            //    ritmo de su destino ocuparía un hueco que otro destino podría estar usando.
            await capacity.WaitAsync(cancellationToken);

            try
            {
                payloads.TryGetValue(delivery.MessageId, out var payload);
                await dispatcher.DispatchAsync(delivery, target, payload, cancellationToken);
            }
            finally
            {
                capacity.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // El lease vencerá y otro worker la recogerá. No se pierde.
        }
#pragma warning disable CA1031 // Un fallo inesperado en una entrega no puede parar el destino entero.
        catch (Exception ex)
        {
            logger.LogError(ex, "Fallo inesperado despachando la entrega {DeliveryId}.", delivery.Id);
        }
#pragma warning restore CA1031
    }
}
