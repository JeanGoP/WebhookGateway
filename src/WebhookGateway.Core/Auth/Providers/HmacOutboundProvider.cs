using WebhookGateway.Core.Domain;

namespace WebhookGateway.Core.Auth.Providers;

/// <summary>
/// Firma el cuerpo saliente con HMAC. Produce exactamente el mismo formato que valida
/// <see cref="Validators.HmacInboundValidator"/>: comparten el firmador.
/// </summary>
public sealed class HmacOutboundProvider(TimeProvider clock) : IOutboundAuthProvider
{
    public OutboundAuthType Type => OutboundAuthType.Hmac;

    public ValueTask ApplyAsync(
        HttpRequestMessage request, OutboundAuthConfig config, ReadOnlyMemory<byte> body,
        int endpointId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var auth = (HmacOutboundAuth)config;

        // Solo se calcula si la plantilla lo pide o hay cabecera donde ponerlo. Un timestamp
        // que no viaja en la petición no lo puede comprobar nadie al otro lado.
        string? timestamp = auth.TimestampHeader is not null || auth.SigningTemplate.Contains("{timestamp}", StringComparison.Ordinal)
            ? clock.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

        var signed = HmacSigner.BuildSignedString(
            auth.SigningTemplate,
            timestamp,
            body.Span,
            request.Method.Method,
            request.RequestUri?.AbsolutePath ?? "/");

        var signature = auth.SignaturePrefix + HmacSigner.Compute(auth.Algorithm, auth.Secret, signed);

        request.Headers.TryAddWithoutValidation(auth.SignatureHeader, signature);

        if (auth.TimestampHeader is not null && timestamp is not null)
        {
            request.Headers.TryAddWithoutValidation(auth.TimestampHeader, timestamp);
        }

        return ValueTask.CompletedTask;
    }
}
