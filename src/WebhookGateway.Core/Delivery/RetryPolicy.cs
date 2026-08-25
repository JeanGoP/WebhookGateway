namespace WebhookGateway.Core.Delivery;

/// <summary>
/// Escalera de reintentos con jitter. Configurable por destino.
/// </summary>
/// <param name="MaxAttempts">Intentos totales, incluido el primero.</param>
/// <param name="Ladder">
/// Esperas por intento. Si se agota la escalera antes que los intentos, se repite el
/// último peldaño.
/// </param>
/// <param name="JitterFactor">
/// Dispersión relativa, entre 0 y 1. Sin ella un pico de fallos genera un pico de
/// reintentos sincronizados que vuelve a tumbar al destino justo cuando se recupera.
/// </param>
public sealed record RetryPolicy(int MaxAttempts, IReadOnlyList<TimeSpan> Ladder, double JitterFactor)
{
    public static readonly RetryPolicy Default = new(
        MaxAttempts: 8,
        Ladder:
        [
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(6),
        ],
        JitterFactor: 0.2);

    /// <summary>
    /// Espera antes del siguiente intento, o <c>null</c> si ya no quedan.
    /// </summary>
    /// <param name="attemptsMade">Intentos ya realizados. Tras el primer fallo vale 1.</param>
    /// <param name="retryAfter">
    /// Valor de <c>Retry-After</c> si el destino lo envió. Manda sobre la escalera: si nos
    /// dice cuándo volver, le hacemos caso.
    /// </param>
    /// <param name="jitterSample">
    /// Muestra uniforme en [0,1). Se recibe en vez de generarse dentro para que la función
    /// sea determinista y testeable.
    /// </param>
    public TimeSpan? NextDelay(int attemptsMade, TimeSpan? retryAfter, double jitterSample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attemptsMade);

        if (attemptsMade >= MaxAttempts)
        {
            return null;
        }

        if (retryAfter is { } explicitDelay)
        {
            return explicitDelay;
        }

        var rung = Ladder[Math.Min(attemptsMade, Ladder.Count - 1)];
        return ApplyJitter(rung, jitterSample);
    }

    /// <inheritdoc cref="NextDelay(int, TimeSpan?, double)"/>
    public TimeSpan? NextDelay(int attemptsMade, TimeSpan? retryAfter = null) =>
        NextDelay(attemptsMade, retryAfter, Random.Shared.NextDouble());

    /// <summary>
    /// Reparte la espera en el rango [1 - factor, 1 + factor] alrededor del peldaño.
    /// </summary>
    private TimeSpan ApplyJitter(TimeSpan value, double sample)
    {
        if (JitterFactor <= 0)
        {
            return value;
        }

        var factor = 1.0 + (((sample * 2.0) - 1.0) * JitterFactor);
        return value * factor;
    }

    /// <summary>
    /// Comprueba si el siguiente intento cabe dentro de la ventana de entrega.
    /// Reintentar pasada la ventana es trabajo que nadie va a aprovechar.
    /// </summary>
    public static bool FitsInWindow(DateTime now, TimeSpan delay, DateTime expiresAt) =>
        now + delay <= expiresAt;
}
