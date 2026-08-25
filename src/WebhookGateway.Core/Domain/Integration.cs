namespace WebhookGateway.Core.Domain;

/// <summary>
/// Agrupador lógico de endpoints: un sistema con el que integramos («ERP», «Shopify»).
/// No participa en el enrutamiento; solo organiza y da un espacio de nombres a los slugs.
/// </summary>
public sealed class Integration
{
    public int Id { get; set; }

    /// <summary>Nombre visible en el panel.</summary>
    public required string Name { get; set; }

    /// <summary>Primer segmento de la URL pública: <c>/in/{slug}/…</c>. Único, minúsculas.</summary>
    public required string Slug { get; set; }

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Días que se conserva la metadata de sus mensajes.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>
    /// Días que se conserva el cuerpo. Más corto que <see cref="RetentionDays"/> a propósito:
    /// pasada la ventana de entrega el cuerpo ya no sirve para reenviar, solo para auditar.
    /// </summary>
    public int PayloadRetentionDays { get; set; } = 90;

    public DateTime CreatedAt { get; set; }

    public ICollection<InboundEndpoint> InboundEndpoints { get; } = [];

    public ICollection<OutboundEndpoint> OutboundEndpoints { get; } = [];
}
