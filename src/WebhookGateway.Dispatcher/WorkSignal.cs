using WebhookGateway.Core.Abstractions;

namespace WebhookGateway.Dispatcher;

/// <summary>
/// La espera del despachador cuando no hay nada que hacer: duerme hasta que la recepción avisa
/// de que acaba de llegar algo o hasta que vence el sondeo, lo que pase antes.
///
/// La cola en memoria solo sirve para no esperar el sondeo. Los identificadores que trae no se
/// usan: la verdad está en SQL, y el ciclo reclama de ahí.
/// </summary>
public sealed class WorkSignal(IDeliveryQueue queue) : IDisposable
{
    private readonly SemaphoreSlim _wakeUp = new(0, 1);
    private Task? _listener;

    /// <summary>Empieza a escuchar la cola. Se llama una vez, al arrancar el bucle.</summary>
    public void StartListening(CancellationToken cancellationToken) =>
        _listener = ListenAsync(cancellationToken);

    /// <summary>Duerme hasta que llegue una señal o venza <paramref name="timeout"/>.</summary>
    /// <returns><see langword="true"/> si despertó por una señal; <see langword="false"/> si venció el plazo.</returns>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _wakeUp.WaitAsync(timeout, cancellationToken);

    /// <summary>Cierra la cola y espera a que el escuchador termine.</summary>
    public async Task StopAsync()
    {
        queue.Complete();

        if (_listener is not null)
        {
            await _listener;
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in queue.ReadAllAsync(cancellationToken))
            {
                // Basta con una señal pendiente: el ciclo reclama de SQL, no de la cola.
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

    public void Dispose() => _wakeUp.Dispose();
}
