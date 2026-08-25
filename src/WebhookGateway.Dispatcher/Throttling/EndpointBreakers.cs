using System.Collections.Concurrent;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Dispatcher.Throttling;

/// <summary>
/// Un cortacircuitos por destino: tras N fallos seguidos deja de intentarlo durante un rato.
/// </summary>
/// <remarks>
/// Evita gastar intentos —y con ellos la ventana de entrega— contra un destino que está
/// caído. Al abrirse, las entregas se reprograman sin consumir intento: no las hemos
/// enviado, así que no cuentan.
/// <para>
/// El estado vive en memoria y se pierde al reiniciar; lo que persiste en SQL es su efecto,
/// porque la reprogramación queda escrita en <c>NextAttemptAt</c>. El coste de olvidarlo es
/// una petición de sondeo por destino tras un despliegue, que es exactamente lo que haría
/// el cortacircuitos al cerrarse de todos modos.
/// </para>
/// </remarks>
public sealed class EndpointBreakers(TimeProvider clock)
{
    private readonly ConcurrentDictionary<int, BreakerState> _states = new();

    /// <summary>Si está abierto, devuelve hasta cuándo. Si no, <see langword="null"/>.</summary>
    public DateTimeOffset? OpenUntil(int endpointId) =>
        _states.TryGetValue(endpointId, out var state) && state.OpenUntil > clock.GetUtcNow()
            ? state.OpenUntil
            : null;

    /// <summary>Apunta el resultado de un intento y abre o cierra el circuito según toque.</summary>
    public void Record(int endpointId, AttemptVerdict verdict, int failureThreshold, int openSeconds)
    {
        /*
            Solo cuentan los fallos transitorios. Un 400 significa que el destino está vivo y
            que el problema es este mensaje concreto: abrir el circuito por eso pararía las
            entregas buenas de todos los demás mensajes.
        */
        if (verdict != AttemptVerdict.Retryable)
        {
            _states.TryRemove(endpointId, out _);
            return;
        }

        _states.AddOrUpdate(
            endpointId,
            _ => Next(new BreakerState(0, DateTimeOffset.MinValue), failureThreshold, openSeconds),
            (_, existing) => Next(existing, failureThreshold, openSeconds));
    }

    private BreakerState Next(BreakerState state, int failureThreshold, int openSeconds)
    {
        var failures = state.ConsecutiveFailures + 1;

        return failures >= failureThreshold
            ? new BreakerState(0, clock.GetUtcNow().AddSeconds(openSeconds))
            : new BreakerState(failures, state.OpenUntil);
    }

    private sealed record BreakerState(int ConsecutiveFailures, DateTimeOffset OpenUntil);
}
