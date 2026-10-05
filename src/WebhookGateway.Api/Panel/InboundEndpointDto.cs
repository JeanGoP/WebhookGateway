using System.Text.Json;
using WebhookGateway.Core.Domain;
using WebhookGateway.Core.Reception;
using WebhookGateway.Data.Security;

namespace WebhookGateway.Api.Panel;

/// <param name="TransientFailureStatusCode">
/// Código con el que se rechaza cuando no se puede guardar. Ausente conserva el actual, <c>0</c>
/// vuelve al global, y cualquier otro valor tiene que ser uno de <see cref="TransientFailureStatus.Allowed"/>.
/// </param>
public sealed record InboundEndpointRequest(
    string? Name,
    string? Slug,
    bool? IsActive,
    InboundAuthType? AuthType,
    JsonElement? AuthConfig,
    DedupeStrategy? DedupeStrategy,
    string? DedupeSource,
    int? MaxBodyBytes,
    int? TransientFailureStatusCode = null);

/// <param name="SecretSet">
/// La API nunca devuelve secretos: solo dice si hay uno guardado. En un <c>PUT</c>, la
/// ausencia de <c>authConfig</c> significa conservar el actual.
/// </param>
/// <param name="TransientFailureStatusCode">Nulo cuando el endpoint usa el código global.</param>
public sealed record InboundEndpointDto(
    int Id,
    int IntegrationId,
    string Name,
    string Slug,
    bool IsActive,
    InboundAuthType AuthType,
    bool SecretSet,
    DedupeStrategy DedupeStrategy,
    string? DedupeSource,
    int MaxBodyBytes,
    int? TransientFailureStatusCode,
    DateTime CreatedAt);

internal static class InboundEndpointDtoExtensions
{
    internal static InboundEndpointDto ToDto(this InboundEndpoint e) => new(
        e.Id, e.IntegrationId, e.Name, e.Slug, e.IsActive,
        e.AuthType,
        e.AuthConfigCipher.Length > 0,
        e.DedupeStrategy,
        e.DedupeSource, e.MaxBodyBytes, e.TransientFailureStatusCode, e.CreatedAt);
}

internal static class InboundEndpointPatch
{
    /// <summary>
    /// Aplica un <c>PUT</c> sobre la entidad. Cada campo ausente se conserva, incluido el
    /// secreto: solo un <c>authConfig</c> nuevo reemplaza al guardado.
    /// </summary>
    internal static void ApplyTo(
        this InboundEndpointRequest request, InboundEndpoint entity, AuthConfigCodec codec)
    {
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            entity.Name = request.Name.Trim();
        }

        if (request.DedupeSource is not null)
        {
            entity.DedupeSource = request.DedupeSource.Trim();
        }

        entity.IsActive = request.IsActive ?? entity.IsActive;
        entity.AuthType = request.AuthType ?? entity.AuthType;
        entity.DedupeStrategy = request.DedupeStrategy ?? entity.DedupeStrategy;
        entity.MaxBodyBytes = request.MaxBodyBytes ?? entity.MaxBodyBytes;

        if (request.TransientFailureStatusCode is { } status)
        {
            entity.TransientFailureStatusCode = ToColumn(status);
        }

        // Regla dura: ausencia del campo = conservar el secreto actual.
        if (request.AuthConfig is { ValueKind: not JsonValueKind.Null } authJson
            && entity.AuthType != InboundAuthType.None)
        {
            entity.AuthConfig = codec.Encode(authJson, entity.AuthType);
        }
    }

    /// <summary>El valor de la petición, tal como se guarda: <c>0</c> es "usar el global".</summary>
    internal static short? ToColumn(int status) => status == 0 ? null : (short)status;

    /// <summary>Mensaje de error si el código pedido no es uno que haga reintentar al emisor.</summary>
    internal static string? TransientStatusError(this InboundEndpointRequest request) =>
        request.TransientFailureStatusCode is { } status && status != 0 && !TransientFailureStatus.IsAllowed(status)
            ? $"El código de rechazo {status} no hace reintentar al emisor. Usa uno de: " +
              $"{string.Join(", ", TransientFailureStatus.Allowed)}, o 0 para el global."
            : null;
}
