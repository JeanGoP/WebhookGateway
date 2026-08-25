using System.Text.Json;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Security;

namespace WebhookGateway.Api.Panel;

public sealed record InboundEndpointRequest(
    string? Name,
    string? Slug,
    bool? IsActive,
    InboundAuthType? AuthType,
    JsonElement? AuthConfig,
    DedupeStrategy? DedupeStrategy,
    string? DedupeSource,
    int? MaxBodyBytes);

/// <param name="SecretSet">
/// La API nunca devuelve secretos: solo dice si hay uno guardado. En un <c>PUT</c>, la
/// ausencia de <c>authConfig</c> significa conservar el actual.
/// </param>
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
    DateTime CreatedAt);

internal static class InboundEndpointDtoExtensions
{
    internal static InboundEndpointDto ToDto(this InboundEndpoint e) => new(
        e.Id, e.IntegrationId, e.Name, e.Slug, e.IsActive,
        e.AuthType,
        e.AuthConfigCipher.Length > 0,
        e.DedupeStrategy,
        e.DedupeSource, e.MaxBodyBytes, e.CreatedAt);
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

        // Regla dura: ausencia del campo = conservar el secreto actual.
        if (request.AuthConfig is { ValueKind: not JsonValueKind.Null } authJson
            && entity.AuthType != InboundAuthType.None)
        {
            entity.AuthConfig = codec.Encode(authJson, entity.AuthType);
        }
    }
}
