using WebhookGateway.Core.Common;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Core.Auth;

/// <summary>
/// Una petición entrante, tal como llegó.
/// </summary>
/// <param name="Body">
/// Los bytes exactos del cuerpo. Nunca se deserializa antes de validar: las firmas HMAC
/// se calculan sobre estos bytes, y deserializar y volver a serializar las rompe.
/// </param>
public readonly record struct InboundRequest(
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Query,
    ReadOnlyMemory<byte> Body,
    string Method,
    string Path,
    string SourceIp);

/// <summary>
/// Valida la autenticación de un webhook entrante. Una implementación por
/// <see cref="InboundAuthType"/>; se resuelven por <see cref="Type"/>.
/// </summary>
public interface IInboundAuthValidator
{
    InboundAuthType Type { get; }

    /// <summary>
    /// Los mensajes de error nunca incluyen el secreto esperado ni la firma calculada:
    /// eso convertiría el endpoint en un oráculo.
    /// </summary>
    ValueTask<Result> ValidateAsync(
        in InboundRequest request,
        InboundAuthConfig config,
        CancellationToken cancellationToken);
}
