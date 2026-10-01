namespace WebhookGateway.Dispatcher.Throttling;

/// <summary>
/// Traduce el límite de un destino —en peticiones por minuto— a lo que entiende un cubo de
/// fichas: cuántas fichas y cada cuánto.
/// </summary>
/// <param name="Period">Cada cuánto se reponen las fichas.</param>
/// <param name="TokensPerPeriod">Cuántas se reponen cada vez.</param>
/// <param name="BurstLimit">Presupuesto acumulable: cuánto se puede gastar de golpe.</param>
public readonly record struct RateBudget(TimeSpan Period, int TokensPerPeriod, int BurstLimit)
{
    /// <summary>
    /// Ráfaga permitida, en segundos de presupuesto. Sin nada de ráfaga la entrega se vuelve
    /// innecesariamente lenta; con demasiada deja de proteger al destino.
    /// </summary>
    private const int BurstSeconds = 5;

    /// <summary>
    /// Tope del periodo de reposición. Un periodo largo cumple el límite igual, pero suelta
    /// todas las fichas de golpe y luego calla: eso es justo lo contrario del suavizado que
    /// este gateway existe para dar.
    /// </summary>
    private const int MaxPeriodSeconds = 10;

    /// <summary>
    /// Calcula el presupuesto de un límite por minuto.
    /// </summary>
    /// <remarks>
    /// Antes esto era <c>ceil(rate / 60)</c> fichas por segundo, que redondea <em>hacia
    /// arriba</em>: un destino configurado a 90/min recibía 120/min, un 33 % más de lo que
    /// había pedido. Ahora el periodo se elige para que la cuenta salga exacta —60 segundos
    /// partido por el máximo común divisor con el límite— y, cuando ese periodo exacto sería
    /// demasiado largo, se recorta <em>hacia abajo</em>. Pasarse del límite de un destino es
    /// un fallo; quedarse un poco corto solo es ir algo más despacio.
    /// </remarks>
    public static RateBudget For(int ratePerMinute)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ratePerMinute);

        var exactPeriod = 60 / Gcd(ratePerMinute, 60);
        var exactTokens = ratePerMinute * exactPeriod / 60;

        var (periodSeconds, tokens) = (exactPeriod, exactTokens);

        if (exactPeriod > MaxPeriodSeconds)
        {
            // División entera: redondea a la baja, que es el lado seguro.
            var capped = ratePerMinute * MaxPeriodSeconds / 60;

            // Con menos de 1 ficha el cubo no repondría nunca. A ritmos tan bajos (≤ 5/min) el
            // periodo exacto es el único que funciona, y la ráfaga ahí da igual.
            if (capped >= 1)
            {
                (periodSeconds, tokens) = (MaxPeriodSeconds, capped);
            }
        }

        // El cubo no puede tener menos capacidad que lo que repone de una vez.
        var burst = Math.Max(tokens, (int)Math.Ceiling(ratePerMinute * BurstSeconds / 60.0));

        return new RateBudget(TimeSpan.FromSeconds(periodSeconds), tokens, burst);
    }

    /// <summary>Peticiones por minuto que concede realmente este presupuesto.</summary>
    public double EffectivePerMinute => TokensPerPeriod * 60.0 / Period.TotalSeconds;

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
