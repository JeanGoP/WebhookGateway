using System.Security.Cryptography;
using System.Text;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Core.Auth;

/// <summary>
/// Firma HMAC compartida entre entrada y salida.
/// </summary>
/// <remarks>
/// Vive en un solo sitio a propósito: el formato que validamos cuando nos firman es el
/// mismo que producimos cuando firmamos nosotros. Tenerlo duplicado sería la forma más
/// fácil de que un día dejaran de coincidir.
/// </remarks>
internal static class HmacSigner
{
    public static string Compute(HmacAlgorithm algorithm, string secret, string data)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var payload = Encoding.UTF8.GetBytes(data);

        /*
            CA5350 marca HMAC-SHA1 como algoritmo débil, y para elegirlo en un diseño nuevo
            tiene razón. Aquí no lo elegimos nosotros: hay emisores que todavía firman así
            y el gateway tiene que poder validar lo que le llega, o esas integraciones
            simplemente no se pueden conectar. Es una opción del catálogo, no el valor por
            defecto, y HMAC-SHA1 sigue sin tener ataques prácticos conocidos —a diferencia
            de SHA-1 a secas, donde el problema son las colisiones.
        */
#pragma warning disable CA5350
        var hash = algorithm switch
        {
            HmacAlgorithm.HmacSha256 => HMACSHA256.HashData(key, payload),
            HmacAlgorithm.HmacSha1 => HMACSHA1.HashData(key, payload),
            HmacAlgorithm.HmacSha512 => HMACSHA512.HashData(key, payload),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Algoritmo HMAC desconocido."),
        };
#pragma warning restore CA5350

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Sustituye los marcadores de la plantilla. Los admitidos son <c>{timestamp}</c>,
    /// <c>{body}</c>, <c>{method}</c> y <c>{path}</c>.
    /// </summary>
    public static string BuildSignedString(
        string template, string? timestamp, ReadOnlySpan<byte> body, string method, string path) =>
        template
            .Replace("{timestamp}", timestamp ?? string.Empty, StringComparison.Ordinal)
            .Replace("{body}", Encoding.UTF8.GetString(body), StringComparison.Ordinal)
            .Replace("{method}", method, StringComparison.Ordinal)
            .Replace("{path}", path, StringComparison.Ordinal);
}
