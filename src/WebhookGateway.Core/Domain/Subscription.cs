namespace WebhookGateway.Core.Domain;

/// <summary>
/// El fanout: qué endpoint de entrada alimenta a qué destino.
/// Cada suscripción activa produce una fila de <see cref="WebhookDelivery"/> por mensaje
/// recibido, y cada una de esas entregas reintenta, expira y se limita por su cuenta.
/// </summary>
public sealed class Subscription
{
    public int Id { get; set; }

    public int InboundEndpointId { get; set; }

    public InboundEndpoint? InboundEndpoint { get; set; }

    public int OutboundEndpointId { get; set; }

    public OutboundEndpoint? OutboundEndpoint { get; set; }

    /// <summary>
    /// Se puede desactivar sin borrarla. Desactivar deja de crear entregas nuevas; las que
    /// ya existen siguen su curso.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Condiciones de filtrado, como JSON. Nulo entrega siempre.
    /// Reservado para F5. El filtro solo lee: nunca modifica el cuerpo, y esa línea es lo
    /// único que lo separa de convertirse en un motor de transformación.
    /// </summary>
    public string? FilterJson { get; set; }

    public DateTime CreatedAt { get; set; }
}
