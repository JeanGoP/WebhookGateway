namespace WebhookGateway.Core.Domain;

/// <summary>
/// La unidad de trabajo del despachador: un par (mensaje × destino).
/// Tabla particionada por mes sobre <see cref="CreatedAt"/>.
/// </summary>
/// <remarks>
/// Con fanout, un mensaje puede producir varias entregas. Cada una lleva su propio reloj
/// de reintentos, su ventana de expiración y su presupuesto de velocidad, para que un
/// destino caído no arrastre a los demás.
/// </remarks>
public sealed class WebhookDelivery
{
    public long Id { get; set; }

    public long MessageId { get; set; }

    public WebhookMessage? Message { get; set; }

    public int OutboundEndpointId { get; set; }

    public OutboundEndpoint? OutboundEndpoint { get; set; }

    /// <summary>Columna de partición. Siempre UTC.</summary>
    public DateTime CreatedAt { get; set; }

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;

    public short AttemptCount { get; set; }

    /// <summary>Cuándo puede volver a reclamarse. El índice del despachador ordena por aquí.</summary>
    public DateTime NextAttemptAt { get; set; }

    /// <summary>
    /// <c>CreatedAt</c> más la ventana del destino. Pasada esta fecha la entrega se marca
    /// <see cref="DeliveryStatus.Expired"/> aunque le queden intentos.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Hasta cuándo es válida la reclamación del worker. Si vence sin renovarse, el
    /// barredor devuelve la entrega a la cola: así una instancia muerta no bloquea nada.
    /// </summary>
    public DateTime? LeaseUntil { get; set; }

    public string? WorkerId { get; set; }

    public short? LastStatusCode { get; set; }

    public string? LastError { get; set; }

    public DateTime? CompletedAt { get; set; }

    public ICollection<DeliveryAttempt> Attempts { get; } = [];

    public bool IsTerminal => Status is DeliveryStatus.Delivered
        or DeliveryStatus.Failed
        or DeliveryStatus.Expired
        or DeliveryStatus.Cancelled;
}
