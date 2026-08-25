namespace WebhookGateway.Core.Abstractions;

/// <summary>
/// Señal de "hay trabajo" entre la recepción y el despachador.
/// </summary>
/// <remarks>
/// Es una optimización de latencia, no la fuente de verdad: SQL Server lo es. Perder un
/// id de esta cola solo retrasa la entrega hasta que el barredor lo recoja, nunca la
/// pierde. Por eso la implementación es un canal en memoria y no un broker.
/// <para>
/// Se encolan identificadores, nunca cuerpos: el worker relee el cuerpo al enviar, y así
/// una ráfaga no se traduce en presión de memoria.
/// </para>
/// </remarks>
public interface IDeliveryQueue
{
    /// <summary>
    /// Encola sin bloquear. Si la cola está llena, descarta y deja constancia: el barredor
    /// recogerá esas entregas. Es preferible perder la señal que frenar la recepción.
    /// </summary>
    void TryEnqueue(IReadOnlyCollection<long> deliveryIds);

    /// <summary>Consume hasta que se cancele. Se completa al apagar de forma ordenada.</summary>
    IAsyncEnumerable<long> ReadAllAsync(CancellationToken cancellationToken);

    /// <summary>Deja de aceptar nuevos ids. Los ya encolados se siguen entregando.</summary>
    void Complete();
}
