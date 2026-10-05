namespace WebhookGateway.Core.Domain;

/// <summary>
/// Cuántos días se guarda cada cosa. Una sola fila, global: la purga vacía particiones mensuales
/// enteras, y una partición lleva mezcladas todas las integraciones de ese mes.
/// </summary>
public sealed class RetentionPolicy
{
    /// <summary>Siempre 1: la tabla tiene una sola fila.</summary>
    public byte Id { get; set; } = 1;

    /// <summary>Mensajes y entregas: lo que se consulta en el explorador.</summary>
    public int MetadataDays { get; set; }

    /// <summary>Cuerpos. Sin ellos un mensaje se puede consultar pero no reenviar.</summary>
    public int PayloadDays { get; set; }

    /// <summary>El detalle de cada intento HTTP.</summary>
    public int AttemptDays { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    /// <summary>Más de cinco años en una base compartida es un error de dedo, no una política.</summary>
    public const int MaxDays = 1825;

    /// <summary>
    /// Por qué una retención no se puede aplicar, o <see langword="null"/> si se puede. Lo que protege
    /// es que la purga no borre algo que todavía hace falta.
    /// </summary>
    /// <param name="longestDeliveryWindowHours">La ventana de entrega más larga de los destinos.</param>
    public static string? Check(int metadataDays, int payloadDays, int attemptDays, int longestDeliveryWindowHours)
    {
        if (new[] { metadataDays, payloadDays, attemptDays }.Any(d => d is < 1 or > MaxDays))
        {
            return $"Cada retención tiene que estar entre 1 y {MaxDays} días.";
        }

        if (payloadDays * 24 < longestDeliveryWindowHours)
        {
            return $"Los cuerpos tienen que guardarse al menos lo que dura la ventana de entrega más larga " +
                   $"({longestDeliveryWindowHours} h): con menos, la purga borraría el cuerpo de una entrega que " +
                   "todavía se está reintentando y ya no se podría enviar.";
        }

        if (metadataDays < payloadDays)
        {
            return "Los mensajes no pueden guardarse menos que sus cuerpos: un cuerpo sin su mensaje no se puede " +
                   "encontrar ni reenviar.";
        }

        if (attemptDays > metadataDays)
        {
            return "Los intentos no pueden guardarse más que las entregas a las que pertenecen.";
        }

        return null;
    }
}
