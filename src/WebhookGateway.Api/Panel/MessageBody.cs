using System.Text;
using WebhookGateway.Data.Traffic;

namespace WebhookGateway.Api.Panel;

/// <param name="Truncated">
/// El cuerpo se cortó por tamaño. Lo que llegó de verdad sigue intacto en la base de
/// datos: el recorte es solo de esta vista.
/// </param>
/// <param name="Body">Texto, cuando el tipo de contenido lo es. Nulo si no.</param>
/// <param name="BodyBase64">Bytes en base64, cuando el cuerpo no es texto. Nulo si lo es.</param>
public sealed record MessageBodyDto(
    long MessageId,
    string ContentType,
    int SizeBytes,
    bool Truncated,
    string? Body,
    string? BodyBase64);

internal static class MessageBodyFactory
{
    /// <summary>
    /// El panel es para mirar un webhook, no para descargar adjuntos. Por encima de esto
    /// se corta: enviar megabytes al navegador para que enseñe las primeras líneas no
    /// ayuda a nadie y sí castiga la memoria del proceso.
    /// </summary>
    internal const int MaxInlineBytes = 256 * 1024;

    internal static MessageBodyDto From(long messageId, OutgoingPayload payload)
    {
        var span = payload.Body.Span;
        var truncated = span.Length > MaxInlineBytes;
        var visible = truncated ? span[..MaxInlineBytes] : span;

        return IsTextual(payload.ContentType)
            ? new MessageBodyDto(messageId, payload.ContentType, span.Length, truncated,
                Encoding.UTF8.GetString(visible), null)
            : new MessageBodyDto(messageId, payload.ContentType, span.Length, truncated,
                null, Convert.ToBase64String(visible));
    }

    /// <summary>
    /// Se decide por el tipo declarado por el emisor, no adivinando sobre los bytes. Si
    /// mintió, el panel enseña base64 y eso ya es información sobre lo que llegó.
    /// </summary>
    private static bool IsTextual(string contentType) =>
        contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
        contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
        contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
}
