using WebhookGateway.Dispatcher.Sending;
using WebhookGateway.Dispatcher.Throttling;

namespace WebhookGateway.Dispatcher;

/// <summary>
/// Cuántas entregas reclama un destino de una vez: las que alcanza a terminar antes de que venza
/// su lease, y nunca más que el tope configurado.
/// </summary>
/// <remarks>
/// Con un tope fijo de veinte, un destino lento se pasaba del lease a mitad de lote: a 6/min,
/// veinte entregas son 200 s y el lease dura 180; con una sola petición a la vez y respuestas de
/// diez segundos, otro tanto. Al vencer, el mantenimiento devuelve esas entregas a la cola y, con
/// dos instancias vivas durante un despliegue, la otra las envía de nuevo. Reclamar menos cuesta
/// solo algún viaje más a SQL, que con el claim por destino es barato.
/// </remarks>
public static class ClaimSizing
{
    /// <summary>
    /// Parte del lease que se reserva para enviar. El resto es margen para reclamar, cargar los
    /// cuerpos y volcar los resultados.
    /// </summary>
    private const double LeaseShare = 0.75;

    public static int For(OutboundTarget target, int maxPerClaim, int leaseSeconds)
    {
        ArgumentNullException.ThrowIfNull(target);

        var budgetSeconds = leaseSeconds * LeaseShare;

        // Por tiempo, en el peor caso: cada entrega agota su timeout. Un destino con un timeout
        // mayor que el lease no cabe nunca; de una en una es lo mejor que se puede hacer con él.
        var rounds = Math.Floor(budgetSeconds / Math.Max(1, target.Timeout.TotalSeconds));
        var byTime = (int)(Math.Max(1, target.MaxConcurrency) * rounds);

        // Por ritmo, sin contar la ráfaga: en un destino con backlog el cubo llega vacío a cada
        // lote, porque el anterior lo gastó.
        var byRate = target.RateLimitPerMinute > 0
            ? (int)(RateBudget.For(target.RateLimitPerMinute).EffectivePerMinute / 60.0 * budgetSeconds)
            : int.MaxValue;

        return Math.Clamp(Math.Min(byTime, byRate), 1, Math.Max(1, maxPerClaim));
    }
}
