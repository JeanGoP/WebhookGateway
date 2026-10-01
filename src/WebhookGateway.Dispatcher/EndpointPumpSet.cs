using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WebhookGateway.Dispatcher;

/// <summary>
/// Las bombas en marcha, una por destino: quién está avanzando ahora mismo y cuánta capacidad
/// pueden gastar entre todas.
/// </summary>
public sealed class EndpointPumpSet(
    EndpointPump pump,
    IOptions<DispatcherOptions> options,
    ILogger<EndpointPumpSet> logger) : IDisposable
{
    /// <summary>Peticiones HTTP en vuelo sumando todos los destinos.</summary>
    private readonly SemaphoreSlim _capacity =
        new(options.Value.MaxGlobalConcurrency, options.Value.MaxGlobalConcurrency);

    /// <summary>
    /// Destinos avanzando a la vez. No es un límite de ritmo —de eso se encarga cada destino— sino
    /// de conexiones: cada bomba abre una para reclamar y otra para volcar resultados, y el pool de
    /// la cadena de conexión no es infinito. Hoy hay un puñado de destinos y este techo no llega a
    /// tocarse; está para que doscientos destinos no se traduzcan en doscientas conexiones.
    /// </summary>
    private readonly SemaphoreSlim _pumpSlots =
        new(options.Value.MaxEndpointsInParallel, options.Value.MaxEndpointsInParallel);

    /// <summary>
    /// Lo que guarda el diccionario no es la bomba, es una señal que se completa cuando esa bomba
    /// termina de verdad. Así <see cref="EnsureRunning"/> puede distinguir "ya está corriendo" de
    /// "ya acabó y hay que volver a arrancarla" sin ninguna carrera.
    /// </summary>
    private readonly ConcurrentDictionary<int, Task> _running = new();

    /// <summary>
    /// Pone en marcha el destino si no lo está ya. No espera: vuelve en cuanto la bomba arranca.
    /// </summary>
    public void EnsureRunning(int endpointId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_running.TryGetValue(endpointId, out var existing))
            {
                if (!existing.IsCompleted)
                {
                    return;
                }

                // Terminó. Se quita solo si sigue siendo esa misma, no vaya a quitarse una nueva.
                _running.TryRemove(new KeyValuePair<int, Task>(endpointId, existing));
                continue;
            }

            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_running.TryAdd(endpointId, finished.Task))
            {
                continue;
            }

            _ = RunAsync(endpointId, finished, cancellationToken);
            return;
        }
    }

    /// <summary>Espera a que todas las bombas terminen. Se usa en el apagado ordenado.</summary>
    public Task WhenAllFinishedAsync() => Task.WhenAll(_running.Values);

    /// <summary>Destinos avanzando ahora mismo. Para los registros, no para decidir nada.</summary>
    public int RunningCount => _running.Count(entry => !entry.Value.IsCompleted);

    private async Task RunAsync(int endpointId, TaskCompletionSource finished, CancellationToken cancellationToken)
    {
        try
        {
            await _pumpSlots.WaitAsync(cancellationToken);

            try
            {
                await pump.DrainAsync(endpointId, _capacity, cancellationToken);
            }
            finally
            {
                _pumpSlots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Apagado normal.
        }
#pragma warning disable CA1031 // Un destino que falla no puede tumbar al resto ni al supervisor.
        catch (Exception ex)
        {
            logger.LogError(ex, "La bomba del destino {EndpointId} terminó con un fallo.", endpointId);
        }
#pragma warning restore CA1031
        finally
        {
            finished.SetResult();
        }
    }

    public void Dispose()
    {
        _capacity.Dispose();
        _pumpSlots.Dispose();
    }
}
