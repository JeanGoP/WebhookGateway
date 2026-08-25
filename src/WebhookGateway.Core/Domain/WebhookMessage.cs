namespace WebhookGateway.Core.Domain;

/// <summary>
/// Un webhook tal como llegó. Inmutable: se escribe una vez y no se vuelve a tocar.
/// Tabla particionada por mes sobre <see cref="ReceivedAt"/>.
/// </summary>
public sealed class WebhookMessage
{
    public long Id { get; set; }

    public int InboundEndpointId { get; set; }

    /// <summary>Columna de partición. Siempre UTC.</summary>
    public DateTime ReceivedAt { get; set; }

    public string SourceIp { get; set; } = string.Empty;

    public string HttpMethod { get; set; } = "POST";

    /// <summary>Cabeceras recibidas, como JSON. Las de autorización se enmascaran antes de guardar.</summary>
    public string HeadersJson { get; set; } = "{}";

    public string? QueryString { get; set; }

    public int BodySizeBytes { get; set; }

    /// <summary>SHA-256 del cuerpo crudo. Sirve para auditar cuando el cuerpo ya se purgó.</summary>
    public byte[] BodyHash { get; set; } = [];

    public MessageStatus Status { get; set; } = MessageStatus.Received;

    public ICollection<WebhookDelivery> Deliveries { get; } = [];
}

/// <summary>
/// El cuerpo, en su propia tabla para poder purgarlo antes que la metadata.
/// </summary>
public sealed class WebhookPayload
{
    public long MessageId { get; set; }

    /// <summary>Columna de partición. Copia de <see cref="WebhookMessage.ReceivedAt"/>.</summary>
    public DateTime ReceivedAt { get; set; }

    public PayloadEncoding Encoding { get; set; } = PayloadEncoding.Gzip;

    public int SizeBytes { get; set; }

    /// <summary>Cuerpo inline. Nulo si está en almacenamiento externo.</summary>
    public byte[]? Body { get; set; }

    /// <summary>
    /// Referencia al almacenamiento externo. Nula si el cuerpo va inline.
    /// La indirección existe desde el primer día para no tener que migrar cuando lleguen
    /// los adjuntos grandes.
    /// </summary>
    public string? StorageRef { get; set; }
}

/// <summary>
/// Índice de deduplicación. Tabla propia, pequeña y sin particionar.
/// </summary>
/// <remarks>
/// Un índice único que cruza particiones impediría el <c>SWITCH</c> de purga, así que la
/// clave vive aquí. Retención corta: ningún proveedor reintenta más allá de unos días.
/// </remarks>
public sealed class MessageDedupe
{
    public int InboundEndpointId { get; set; }

    public required string DedupeKey { get; set; }

    /// <summary>Id del mensaje original, el que se devuelve al emisor cuando repite.</summary>
    public long MessageId { get; set; }

    public DateTime ExpiresAt { get; set; }
}
