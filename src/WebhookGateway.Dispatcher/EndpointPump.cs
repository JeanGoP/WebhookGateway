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
/// Esto es lo que sustituye al ciclo anterior, que reclamaba cien entregas de todos los destinos
/// mezclados y esperaba a que acabara la más lenta. Con aquello, un destino limitado a 60/min y con
/// backlog alargaba cada ciclo unos quince segundos, y uno con un timeout de 30 s lo alargaba
/// treinta: el sistema entero caía a tres o siete entregas por segundo aunque los demás destinos
/// estuvieran perfectamente.
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
    /// del destino está abierto o cuando se pide el apagado.
    /// </summary>
    public async Task DrainAsync(int endpointId, SemaphoreSlim capacity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capacity);

        while (!cancellationToken.IsCancellationRequested)
        {
            /*
                Circuito abierto: no se reclama ni una fila. Antes esto se comprobaba por entrega, ya
                reclamada, así que un destino caído con mil pendientes eran mil reclamaciones y mil
                reprogramaciones en SQL para no enviar nada.
            */
            if (breakers.OpenUntil(endpointId) is not null)
            {
                return;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var leaseUntil = now.AddSeconds(_options.LeaseSeconds);

            var claimed = await claimer.ClaimForEndpointAsync(
                endpointId, now, leaseUntil, _options.WorkerId, _options.MaxPerEndpointPerClaim, cancellationToken);

            if (claimed.Count == 0)
            {
                return;
            }

            var target = await targets.GetAsync(endpointId, cancellationToken);

            if (target is null)
            {
                // El destino ya no existe o lo desactivaron. No hay a dónde mandar esto.
                foreach (var delivery in claimed)
                {
                    dispatcher.TerminateMissingTarget(delivery);
                }

                await recorder.FlushAsync(cancellationToken);
                return;
            }

            var payloads = await dispatcher.PreloadPayloadsAsync(claimed.Select(d => d.MessageId), cancellationToken);

            /*
                La concurrencia la pone el destino, no los núcleos de la máquina: entregar es esperar
                a la red. El techo global de capacidad va aparte, dentro de SendOneAsync.
            */
            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, target.MaxConcurrency),
                CancellationToken = cancellationToken,
            };

            await Parallel.ForEachAsync(claimed, parallel, (delivery, token) =>
                SendOneAsync(delivery, target, payloads, capacity, token));

            await recorder.FlushAsync(cancellationToken);
        }
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

            /*
                2. La capacidad global, y DESPUÉS del turno. El orden es lo importante: si se pidiera
                   primero la capacidad, una entrega esperando el ritmo de su destino estaría ocupando
                   un hueco que otro destino podría usar, y el problema que esto viene a arreglar
                   volvería por la puerta de atrás.
            */
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
