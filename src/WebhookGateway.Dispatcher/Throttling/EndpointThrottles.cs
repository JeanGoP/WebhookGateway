using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using WebhookGateway.Dispatcher.Sending;

namespace WebhookGateway.Dispatcher.Throttling;

/// <summary>
/// El freno por destino: cuántas peticiones simultáneas y a qué ritmo.
/// </summary>
/// <remarks>
/// Esto es el producto. Absorber un pico de 2.000 mensajes por minuto y drenarlo a un
/// destino que solo tolera 60 es exactamente para lo que existe el gateway, y este es el
/// sitio donde ocurre.
/// <para>
/// El cubo permite una ráfaga de cinco segundos de presupuesto antes de imponer el ritmo:
/// sin nada de ráfaga la entrega se vuelve innecesariamente lenta, y con demasiada deja de
/// proteger al destino.
/// </para>
/// </remarks>
public sealed class EndpointThrottles : IDisposable
{
    private const int BurstSeconds = 5;

    private readonly ConcurrentDictionary<int, Throttle> _throttles = new();

    /// <summary>
    /// Espera turno. El resultado hay que liberarlo siempre; el <c>using</c> en la llamada
    /// se encarga.
    /// </summary>
    public async Task<ThrottleLease> AcquireAsync(OutboundTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var throttle = _throttles.AddOrUpdate(
            target.Id,
            _ => Throttle.For(target),
            (_, existing) => existing.Matches(target) ? existing : Replace(existing, target));

        await throttle.Slots.WaitAsync(cancellationToken);

        if (throttle.Limiter is null)
        {
            return new ThrottleLease(throttle.Slots, null);
        }

        var lease = await throttle.Limiter.AcquireAsync(1, cancellationToken);

        if (lease.IsAcquired)
        {
            return new ThrottleLease(throttle.Slots, lease);
        }

        // La cola del limitador está llena: hay más entregas esperando de las que este
        // destino puede absorber. Se devuelve el hueco y el despachador la reprograma.
        lease.Dispose();
        throttle.Slots.Release();

        return ThrottleLease.NotAcquired;
    }

    public void Dispose()
    {
        foreach (var throttle in _throttles.Values)
        {
            throttle.Dispose();
        }

        _throttles.Clear();
    }

    private static Throttle Replace(Throttle existing, OutboundTarget target)
    {
        // La configuración cambió desde el panel. El viejo se desecha; las entregas que aún
        // lo estén usando terminan con él y la siguiente ya usa el nuevo.
        existing.Dispose();
        return Throttle.For(target);
    }

    private sealed class Throttle(SemaphoreSlim slots, TokenBucketRateLimiter? limiter, int concurrency, int perMinute) : IDisposable
    {
        public SemaphoreSlim Slots { get; } = slots;

        public TokenBucketRateLimiter? Limiter { get; } = limiter;

        public bool Matches(OutboundTarget target) =>
            target.MaxConcurrency == concurrency && target.RateLimitPerMinute == perMinute;

        public static Throttle For(OutboundTarget target)
        {
            var slots = new SemaphoreSlim(target.MaxConcurrency, target.MaxConcurrency);

            // Cero o menos significa sin límite de ritmo; solo queda el de concurrencia.
            if (target.RateLimitPerMinute <= 0)
            {
                return new Throttle(slots, null, target.MaxConcurrency, target.RateLimitPerMinute);
            }

            var perSecond = Math.Max(1, (int)Math.Ceiling(target.RateLimitPerMinute / 60.0));

            var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = perSecond * BurstSeconds,
                TokensPerPeriod = perSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                AutoReplenishment = true,
                QueueLimit = Math.Max(target.MaxConcurrency, 64),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });

            return new Throttle(slots, limiter, target.MaxConcurrency, target.RateLimitPerMinute);
        }

        public void Dispose()
        {
            Limiter?.Dispose();
            Slots.Dispose();
        }
    }
}

/// <summary>Turno concedido por <see cref="EndpointThrottles"/>. Liberarlo es obligatorio.</summary>
public sealed class ThrottleLease(SemaphoreSlim? slots, RateLimitLease? lease) : IDisposable
{
    public static readonly ThrottleLease NotAcquired = new(null, null);

    public bool IsAcquired => slots is not null;

    public void Dispose()
    {
        lease?.Dispose();
        slots?.Release();
    }
}
