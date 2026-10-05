using WebhookGateway.Core.Reception;

namespace WebhookGateway.Api.Reception;

/// <summary>Ajustes de la recepción (<c>/in/*</c>).</summary>
public sealed class ReceptionOptions
{
    public const string SectionName = "Gateway:Reception";

    /// <summary>
    /// Código con el que se rechaza un webhook que no se pudo guardar cuando su endpoint no elige
    /// otro, o cuando no se sabe cuál eligió porque el proceso arrancó con SQL ya caído.
    /// </summary>
    /// <remarks>
    /// 503 por defecto, que es lo estándar y lo que reintentan casi todos los emisores. Si el emisor
    /// principal es una app del Marketplace de GHL conviene 429: es lo único que GHL reintenta, y
    /// este valor es el que se usa justo cuando no hay configuración en memoria a la que acudir.
    /// </remarks>
    public int TransientFailureStatusCode { get; set; } = TransientFailureStatus.Default;
}
