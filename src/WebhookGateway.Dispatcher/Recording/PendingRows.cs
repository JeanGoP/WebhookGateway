namespace WebhookGateway.Dispatcher.Recording;

/// <summary>Una fila de <c>DeliveryAttempt</c> pendiente de escribir.</summary>
public sealed record AttemptRecord(
    long DeliveryId,
    DateTime StartedAt,
    short AttemptNumber,
    int DurationMs,
    short? StatusCode,
    string? ResponseHeadersJson,
    string? ResponseBody,
    string? ErrorMessage,
    string WorkerId);

/// <summary>El nuevo estado de una entrega tras un intento.</summary>
public sealed record DeliveryUpdate(
    long Id,
    DateTime CreatedAt,
    byte Status,
    short AttemptCount,
    DateTime NextAttemptAt,
    short? LastStatusCode,
    string? LastError,
    DateTime? CompletedAt);

/// <summary>
/// Recorta los textos de un resultado al ancho de su columna antes de escribirlo.
/// </summary>
/// <remarks>
/// Estos topes son las anchuras de las columnas, no números elegidos: WebhookDelivery.LastError y
/// DeliveryAttempt.ErrorMessage son nvarchar(1000), y las dos de respuesta nvarchar(4000). Recortar
/// de más pierde información; recortar de menos revienta el insert del lote entero.
/// </remarks>
internal static class ColumnLimits
{
    private const int MaxErrorLength = 1000;
    private const int MaxResponseLength = 4000;

    public static DeliveryUpdate Fit(DeliveryUpdate update) =>
        update.LastError is { Length: > MaxErrorLength } error
            ? update with { LastError = error[..MaxErrorLength] }
            : update;

    public static AttemptRecord Fit(AttemptRecord attempt) =>
        attempt with
        {
            ResponseHeadersJson = Cut(attempt.ResponseHeadersJson, MaxResponseLength),
            ResponseBody = Cut(attempt.ResponseBody, MaxResponseLength),
            ErrorMessage = Cut(attempt.ErrorMessage, MaxErrorLength),
        };

    private static string? Cut(string? text, int max) =>
        text is { Length: var length } && length > max ? text[..max] : text;
}
