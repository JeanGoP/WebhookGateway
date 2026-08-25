namespace WebhookGateway.Core.Domain;

/// <summary>
/// Un intento HTTP concreto. Es lo que se ve en el panel al depurar una entrega.
/// Tabla particionada por mes sobre <see cref="StartedAt"/>.
/// </summary>
/// <remarks>
/// Se escriben en batch, nunca de uno en uno: es la tabla con más volumen del sistema y
/// el grueso de la carga que podemos quitarle al servidor SQL.
/// </remarks>
public sealed class DeliveryAttempt
{
    public long Id { get; set; }

    public long DeliveryId { get; set; }

    /// <summary>Columna de partición. Siempre UTC.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>1 para el primer intento.</summary>
    public short AttemptNumber { get; set; }

    public int DurationMs { get; set; }

    /// <summary>Nulo si no hubo respuesta: timeout o error de red.</summary>
    public short? StatusCode { get; set; }

    public string? ResponseHeadersJson { get; set; }

    /// <summary>Cuerpo de la respuesta, truncado. Suficiente para depurar, no para archivar.</summary>
    public string? ResponseBody { get; set; }

    public string? ErrorMessage { get; set; }

    public string? WorkerId { get; set; }

    /// <summary>Longitud máxima que se guarda de la respuesta.</summary>
    public const int MaxResponseBodyLength = 4000;

    public static string? TruncateResponse(string? body) => body switch
    {
        null => null,
        { Length: <= MaxResponseBodyLength } => body,
        _ => string.Concat(body.AsSpan(0, MaxResponseBodyLength - 3), "..."),
    };
}
