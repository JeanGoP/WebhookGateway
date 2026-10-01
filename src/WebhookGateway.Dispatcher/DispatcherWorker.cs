using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebhookGateway.Core.Abstractions;
using WebhookGateway.Dispatcher.Claiming;
using WebhookGateway.Dispatcher.Recording;

namespace WebhookGateway.Dispatcher;

/// <summary>
/// El supervisor: mira qué destinos tienen trabajo vencido y pone en marcha el de cada uno. No
/// entrega nada él mismo.
/// </summary>
/// <remarks>
/// Antes este bucle era el despachador entero: reclamaba cien entregas de todos los destinos
/// mezclados, las enviaba en paralelo y esperaba a que acabara la más lenta. Ahora cada destino
/// avanza en su propia bomba (<see cref="EndpointPump"/>) y lo único que hace este bucle es
/// descubrir quién tiene trabajo, además del mantenimiento de leases y caducidades.
/// </remarks>
public sealed class DispatcherWorker(
    DeliveryClaimer claimer,
    EndpointPumpSet pumps,
    DeliveryRecorder recorder,
    IDeliveryQueue queue,
    TimeProvider clock,
    IOptions<DispatcherOptions> options,
    ILogger<DispatcherWorker> logger) : BackgroundService
{
    /// <summary>
    /// Espera mínima entre dos pasadas de descubrimiento cuando la recepción avisa. Sin esto, un
    /// pico de setenta mensajes por segundo serían setenta consultas de descubrimiento por segundo:
    /// la señal se agrupa y una sola pasada ve todo lo que acaba de llegar.
    /// </summary>
    private static readonly TimeSpan SignalCoalescing = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherOptions _options = options.Value;
    private readonly WorkSignal _signal = new(queue);
    private readonly CycleBackoff _backoff = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("El despachador está desactivado por configuración. Esta instancia solo recibe.");
            return;
        }

        logger.LogInformation(
            "Despachador {WorkerId} en marcha: hasta {Global} entregas en vuelo y {Endpoints} destinos a la vez.",
            _options.WorkerId, _options.MaxGlobalConcurrency, _options.MaxEndpointsInParallel);

        _signal.StartListening(stoppingToken);
        var nextMaintenance = DateTimeOffset.MinValue;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                nextMaintenance = await RunIterationAsync(nextMaintenance, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Apagado normal.
        }
        finally
        {
            await _signal.StopAsync();
            await ShutDownAsync();
        }
    }

    /// <summary>
    /// Una pasada del supervisor, con todo lo que pueda fallar contenido aquí dentro.
    ///
    /// Esto es lo que impide que una excepción escape de <c>ExecuteAsync</c>. El host trata un
    /// <c>BackgroundService</c> que termina con fallo parando el proceso entero, y en este proceso
    /// también vive la recepción: un timeout de SQL dejaría al gateway sin recibir webhooks, no solo
    /// sin despacharlos. Mejor esperar y volver a intentarlo.
    /// </summary>
    /// <returns>Cuándo toca la siguiente pasada de mantenimiento.</returns>
    private async Task<DateTimeOffset> RunIterationAsync(DateTimeOffset nextMaintenance, CancellationToken stoppingToken)
    {
        try
        {
            if (clock.GetUtcNow() >= nextMaintenance)
            {
                await RunMaintenanceAsync(stoppingToken);
                nextMaintenance = clock.GetUtcNow().AddSeconds(_options.MaintenanceIntervalSeconds);
            }

            await DiscoverAsync(stoppingToken);
            _backoff.Reset();

            // La espera es siempre, haya trabajo o no: las bombas ya están avanzando por su cuenta y
            // lo único que hace falta es volver a mirar de vez en cuando.
            var woken = await _signal.WaitAsync(TimeSpan.FromSeconds(_options.IdlePollSeconds), stoppingToken);

            if (woken)
            {
                await Task.Delay(SignalCoalescing, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Deliberado: el bucle sobrevive a cualquier fallo, pase lo que pase.
        catch (Exception ex)
        {
            var wait = _backoff.NextDelay();

            logger.LogError(
                ex,
                "Fallo en el ciclo del despachador ({Count} seguidos). Se reintenta en {Seconds:F0} s.",
                _backoff.ConsecutiveFailures,
                wait.TotalSeconds);

            await Task.Delay(wait, clock, stoppingToken);
        }
#pragma warning restore CA1031

        return nextMaintenance;
    }

    /// <summary>Pone en marcha una bomba por cada destino con trabajo vencido.</summary>
    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var endpoints = await claimer.FindEndpointsWithWorkAsync(now, cancellationToken);

        foreach (var endpointId in endpoints)
        {
            pumps.EnsureRunning(endpointId, cancellationToken);
        }

        if (endpoints.Count > 0)
        {
            logger.LogDebug(
                "{Pending} destinos con trabajo pendiente, {Running} avanzando.",
                endpoints.Count, pumps.RunningCount);
        }
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

    /// <summary>
    /// Apagado ordenado: esperar a que las bombas terminen lo que tienen en vuelo, volcar lo
    /// pendiente y soltar los leases que este worker aún tenga. Sin esto, las entregas en vuelo al
    /// desplegar esperarían a que venciera su lease.
    /// </summary>
    private async Task ShutDownAsync()
    {
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await pumps.WhenAllFinishedAsync();
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
        _signal.Dispose();
        base.Dispose();
    }
}
