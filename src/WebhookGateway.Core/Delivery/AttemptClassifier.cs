using System.Net;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Core.Delivery;

/// <summary>
/// Traduce la respuesta de un destino a la decisión de reintentar o no.
/// Función pura: toda la política de reintentos empieza aquí y se testea sin red.
/// </summary>
public static class AttemptClassifier
{
    /// <param name="statusCode">Código HTTP, o <c>null</c> si no hubo respuesta (timeout, error de red).</param>
    public static AttemptVerdict Classify(int? statusCode)
    {
        // Sin respuesta: el destino no llegó a contestar. Siempre transitorio.
        if (statusCode is null)
        {
            return AttemptVerdict.Retryable;
        }

        var code = statusCode.Value;

        if (code is >= 200 and < 300)
        {
            return AttemptVerdict.Success;
        }

        // 3xx sin seguir: el destino está mal configurado, no se arregla insistiendo.
        if (code is >= 300 and < 400)
        {
            return AttemptVerdict.Permanent;
        }

        if (code is >= 400 and < 500)
        {
            // Las dos únicas excepciones: el destino pide que volvamos más tarde.
            return code is (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests
                ? AttemptVerdict.Retryable
                : AttemptVerdict.Permanent;
        }

        // 5xx y cualquier cosa fuera de rango: transitorio.
        return AttemptVerdict.Retryable;
    }

    /// <summary>
    /// Interpreta <c>Retry-After</c>, que puede venir en segundos o como fecha HTTP.
    /// Devuelve <c>null</c> si falta o no es interpretable.
    /// </summary>
    public static TimeSpan? ParseRetryAfter(string? headerValue, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        if (int.TryParse(headerValue, out var seconds))
        {
            return seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
        }

        if (DateTimeOffset.TryParse(headerValue, out var when))
        {
            var delay = when - now;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }
}
