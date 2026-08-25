using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebhookGateway.Core.Abstractions;
using WebhookGateway.Dispatcher.Claiming;
using WebhookGateway.Dispatcher.Recording;

namespace WebhookGateway.Dispatcher;

/// <summary>
/// El bucle del despachador: reclama un lote, lo entrega en paralelo, vuelca los resultados
/// y espera a que haya más trabajo.
/// </summary>
public sealed class DispatcherWorker(
    DeliveryClaimer claimer,
    DeliveryDispatcher dispatcher,
    DeliveryRecorder recorder,
    IDeliveryQueue queue,
    TimeProvider clock,
    IOptions<DispatcherOptions> options,
    ILogger<DispatcherWorker> logger) : BackgroundService
{
    private readonly DispatcherOptions _options = options.Value;
    private readonly SemaphoreSlim _wakeUp = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("El despachador está desactivado por configuración. Esta instancia solo recibe.");
            return;
        }

        logger.LogInformation("Despachador {WorkerId} en marcha.", _options.WorkerId);

        // La cola en memoria solo sirve para no esperar el sondeo cuando acaba de llegar
        // algo. Los identificadores en sí no se usan: la verdad está en SQL.
        var listener = ListenForSignalsAsync(stoppingToken);
        var nextMaintenance = DateTimeOffset.MinValue;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (clock.GetUtcNow() >= nextMaintenance)
                {
                    await RunMaintenanceAsync(stoppingToken);
                    nextMaintenance = clock.GetUtcNow().AddSeconds(_options.MaintenanceIntervalSeconds);
                }

                var dispatched = await RunCycleAsync(stoppingToken);

                if (dispatched == 0)
                {
                    await WaitForWorkAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Apagado normal.
        }
        finally
        {
            queue.Complete();
            await listener;
            await ShutDownAsync();
        }
    }

    private async Task<int> RunCycleAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var leaseUntil = now.AddSeconds(_options.LeaseSeconds);

        var claimed = await claimer.ClaimAsync(
            now, leaseUntil, _options.WorkerId, _options.BatchSize, _options.MaxPerEndpointPerClaim, cancellationToken);

        if (claimed.Count == 0)
        {
            return 0;
        }

        /*
            Sin límite de paralelismo aquí a propósito: el freno real es el de cada destino,
            en EndpointThrottles. Un lote de cien entregas repartidas entre veinte destinos
            debe poder avanzar a la vez; lo que no puede es saturar a ninguno de ellos.
        */
        await Parallel.ForEachAsync(claimed, cancellationToken, async (delivery, token) =>
        {
            try
            {
                await dispatcher.DispatchAsync(delivery, token);
            }
            catch (OperationCanceledException)
            {
                // El lease vencerá y otro worker la recogerá. No se pierde.
            }
#pragma warning disable CA1031 // Un fallo inesperado en una entrega no puede parar el bucle.
            catch (Exception ex)
            {
                logger.LogError(ex, "Fallo inesperado despachando la entrega {DeliveryId}.", delivery.Id);
            }
#pragma warning restore CA1031
        });

        await recorder.FlushAsync(cancellationToken);

        return claimed.Count;
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        var recovered = await claimer.RecoverOrphanedLeasesAsync(now, _options.MaintenanceBatchSize, cancellationToken);
        var expired = await claimer.ExpireOverdueAsync(now, _options.MaintenanceBatchSize, cancellationToken);

        if (recovered > 0)
        {
            logger.LogWarning("Se recuperaron {Count} entregas cuyo worker murió sin liberarlas.", recovered);
        }

        if (expired > 0)
        {
            logger.LogInformation("{Count} entregas agotaron su ventana y se marcaron como caducadas.", expired);
        }
    }

    /// <summary>Duerme hasta que llegue algo nuevo o venza el sondeo, lo que pase antes.</summary>
    private async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _wakeUp.WaitAsync(TimeSpan.FromSeconds(_options.IdlePollSeconds), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private async Task ListenForSignalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in queue.ReadAllAsync(cancellationToken))
            {
                // Basta con una señal: el ciclo reclama de SQL, no de la cola.
                if (_wakeUp.CurrentCount == 0)
                {
                    _wakeUp.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Apagado normal.
        }
    }

    /// <summary>
    /// Apagado ordenado: volcar lo pendiente y soltar los leases que este worker aún tenga.
    /// Sin esto, las entregas en vuelo al desplegar esperarían a que venciera su lease.
    /// </summary>
    private async Task ShutDownAsync()
    {
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await recorder.FlushAsync(grace.Token);

            var released = await claimer.ReleaseAllAsync(_options.WorkerId, grace.Token);

            logger.LogInformation("Despachador detenido. {Count} entregas devueltas a la cola.", released);
        }
#pragma warning disable CA1031 // Apagando: registrar y salir es todo lo que se puede hacer.
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo cerrar limpiamente. Los leases se liberarán al vencer.");
        }
#pragma warning restore CA1031
    }

    public override void Dispose()
    {
        _wakeUp.Dispose();
        base.Dispose();
    }
}
