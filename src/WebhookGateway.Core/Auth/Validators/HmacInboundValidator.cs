using System.Security.Cryptography;
using System.Text;
using WebhookGateway.Core.Common;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Core.Auth.Validators;

/// <summary>
/// Verifica una firma HMAC sobre el cuerpo crudo, con tolerancia de timestamp para que una
/// firma capturada no pueda reproducirse indefinidamente.
/// </summary>
public sealed class HmacInboundValidator(TimeProvider clock) : IInboundAuthValidator
{
    public InboundAuthType Type => InboundAuthType.Hmac;

    public ValueTask<Result> ValidateAsync(in InboundRequest request, InboundAuthConfig config, CancellationToken cancellationToken)
    {
        var auth = (HmacInboundAuth)config;

        if (!request.Headers.TryGetValue(auth.SignatureHeader, out var received) || received.Length == 0)
        {
            return ValueTask.FromResult(Result.Fail("auth.missing_signature", $"Falta la cabecera {auth.SignatureHeader}."));
        }

        string? timestamp = null;
        if (auth.TimestampHeader is not null)
        {
            if (!request.Headers.TryGetValue(auth.TimestampHeader, out timestamp))
            {
                return ValueTask.FromResult(Result.Fail("auth.missing_timestamp", $"Falta la cabecera {auth.TimestampHeader}."));
            }

            var tolerance = CheckTolerance(timestamp, auth.ToleranceSeconds);
            if (tolerance.IsFailure)
            {
                return ValueTask.FromResult(tolerance);
            }
        }

        var signed = HmacSigner.BuildSignedString(auth.SigningTemplate, timestamp, request.Body.Span, request.Method, request.Path);
        var expected = auth.SignaturePrefix + HmacSigner.Compute(auth.Algorithm, auth.Secret, signed);

        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(received), Encoding.UTF8.GetBytes(expected));

        return ValueTask.FromResult(ok ? Result.Ok() : Result.Fail("auth.invalid_signature", "La firma no coincide."));
    }

    private Result CheckTolerance(string timestampValue, int toleranceSeconds)
    {
        // Cero desactiva la comprobación. Solo para emisores que no mandan timestamp.
        if (toleranceSeconds <= 0)
        {
            return Result.Ok();
        }

        if (!long.TryParse(timestampValue, out var unixSeconds))
        {
            return Result.Fail("auth.malformed_timestamp", "El timestamp de la firma no es un número.");
        }

        var signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var drift = (clock.GetUtcNow() - signedAt).Duration();

        return drift.TotalSeconds <= toleranceSeconds
            ? Result.Ok()
            : Result.Fail("auth.expired_signature", "El timestamp de la firma está fuera de la ventana admitida.");
    }
}
