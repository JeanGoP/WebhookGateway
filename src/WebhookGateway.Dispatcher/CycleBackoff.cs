namespace WebhookGateway.Dispatcher;

/// <summary>
/// Escalera de espera para los fallos del propio bucle del despachador: SQL que no responde,
/// un timeout, un deadlock. Sube con cada fallo seguido y se reinicia con el primer ciclo bueno,
/// de modo que un tropiezo aislado no frena nada y una caída de SQL no se convierte en un
/// martilleo de reintentos.
/// </summary>
public sealed class CycleBackoff
{
    /// <summary>
    /// Si se agota la escalera se repite el último peldaño. Un minuto es el techo: más allá,
    /// el backlog crecería sin que nadie lo estuviera intentando.
    /// </summary>
    private static readonly TimeSpan[] Ladder =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
    ];

    private const double JitterFactor = 0.2;

    /// <summary>Fallos seguidos sin un ciclo bueno en medio. Se registra para poder alertar.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>
    /// Cuenta un fallo y devuelve cuánto esperar antes de volver a intentarlo.
    /// </summary>
    /// <param name="jitterSample">
    /// Muestra uniforme en [0,1). Se recibe en vez de generarse dentro para que la función sea
    /// determinista y testeable.
    /// </param>
    public TimeSpan NextDelay(double jitterSample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(jitterSample);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(jitterSample, 1.0);

        ConsecutiveFailures++;

        var rung = Ladder[Math.Min(ConsecutiveFailures - 1, Ladder.Length - 1)];

        // Jitter: durante un despliegue hay dos instancias vivas y les falla lo mismo a la vez.
        // Sin dispersión volverían las dos justo en el mismo instante.
        var factor = 1.0 + (((jitterSample * 2.0) - 1.0) * JitterFactor);
        return rung * factor;
    }

    /// <inheritdoc cref="NextDelay(double)"/>
    public TimeSpan NextDelay() => NextDelay(Random.Shared.NextDouble());

    /// <summary>Un ciclo bueno borra la cuenta: la escalera vuelve al primer peldaño.</summary>
    public void Reset() => ConsecutiveFailures = 0;
}
