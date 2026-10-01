namespace WebhookGateway.Dispatcher.Notifications;

/// <summary>
/// Cuántas entregas descartadas se han callado por destino, y cuándo toca volver a avisar.
/// </summary>
/// <remarks>
/// Un destino que responde 400 a todo descarta una entrega por mensaje. Sin agrupar, eso es un
/// correo por entrega y por destinatario: miles. Agrupando, uno por destino cada X minutos, y el
/// aviso dice cuántas más hubo mientras callaba, que es la información que de verdad hace falta.
/// </remarks>
public sealed class DeadLetterWindows(TimeProvider clock)
{
    private readonly Dictionary<int, Window> _windows = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Decide si toca avisar de este destino.
    /// </summary>
    /// <param name="window">
    /// Cuánto callar tras un aviso. <see cref="TimeSpan.Zero"/> o menos avisa siempre.
    /// </param>
    /// <returns>
    /// <see langword="null"/> si toca callar. Si toca avisar, cuántas descartadas se callaron desde
    /// el aviso anterior: 0 la primera vez.
    /// </returns>
    public int? TryClaimAlert(int endpointId, TimeSpan window)
    {
        var now = clock.GetUtcNow();

        if (window <= TimeSpan.Zero)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_windows.TryGetValue(endpointId, out var abierta) && now < abierta.SilentUntil)
            {
                // Dentro de la ventana: se cuenta y se calla.
                _windows[endpointId] = abierta with { Suppressed = abierta.Suppressed + 1 };
                return null;
            }

            var calladas = _windows.TryGetValue(endpointId, out var anterior) ? anterior.Suppressed : 0;

            _windows[endpointId] = new Window(now + window, 0);

            return calladas;
        }
    }

    /// <summary>Lo que un destino lleva callado ahora mismo. Para los registros, no para decidir.</summary>
    public int SuppressedFor(int endpointId)
    {
        lock (_gate)
        {
            return _windows.TryGetValue(endpointId, out var w) ? w.Suppressed : 0;
        }
    }

    private readonly record struct Window(DateTimeOffset SilentUntil, int Suppressed);
}
