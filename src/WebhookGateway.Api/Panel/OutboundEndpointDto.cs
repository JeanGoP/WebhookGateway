using System.Text.Json;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Security;

namespace WebhookGateway.Api.Panel;

public sealed record OutboundEndpointRequest(
    string? Name,
    string? TargetUrl,
    string? HttpMethod,
    bool? IsActive,
    OutboundAuthType? AuthType,
    JsonElement? AuthConfig,
    string? CustomHeadersJson,
    int? RateLimitPerMinute,
    int? MaxConcurrency,
    int? TimeoutSeconds,
    int? MaxAttempts,
    int? DeliveryWindowHours,
    string? BackoffLadderJson,
    int? BreakerFailureThreshold,
    int? BreakerOpenSeconds);

/// <param name="SecretSet">
/// La API nunca devuelve secretos: solo dice si hay uno guardado. En un <c>PUT</c>, la
/// ausencia de <c>authConfig</c> significa conservar el actual.
/// </param>
public sealed record OutboundEndpointDto(
    int Id,
    int IntegrationId,
    string Name,
    string TargetUrl,
    string HttpMethod,
    bool IsActive,
    OutboundAuthType AuthType,
    bool SecretSet,
    string? CustomHeadersJson,
    int RateLimitPerMinute,
    int MaxConcurrency,
    int TimeoutSeconds,
    int MaxAttempts,
    int DeliveryWindowHours,
    string? BackoffLadderJson,
    int BreakerFailureThreshold,
    int BreakerOpenSeconds,
    DateTime CreatedAt);

internal static class OutboundEndpointDtoExtensions
{
    internal static OutboundEndpointDto ToDto(this OutboundEndpoint e) => new(
        e.Id, e.IntegrationId, e.Name, e.TargetUrl, e.HttpMethod, e.IsActive,
        e.AuthType,
        e.AuthConfigCipher.Length > 0,
        e.CustomHeadersJson,
        e.RateLimitPerMinute, e.MaxConcurrency, e.TimeoutSeconds,
        e.MaxAttempts, e.DeliveryWindowHours, e.BackoffLadderJson,
        e.BreakerFailureThreshold, e.BreakerOpenSeconds, e.CreatedAt);
}

internal static class OutboundEndpointPatch
{
    /// <summary>
    /// Aplica un <c>PUT</c> sobre la entidad. Cada campo ausente se conserva, incluido el
    /// secreto: solo un <c>authConfig</c> nuevo reemplaza al guardado.
    /// </summary>
    internal static void ApplyTo(
        this OutboundEndpointRequest request, OutboundEndpoint entity, AuthConfigCodec codec)
    {
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            entity.Name = request.Name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.TargetUrl))
        {
            entity.TargetUrl = request.TargetUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.HttpMethod))
        {
            entity.HttpMethod = request.HttpMethod.Trim().ToUpperInvariant();
        }

        entity.IsActive = request.IsActive ?? entity.IsActive;
        entity.AuthType = request.AuthType ?? entity.AuthType;
        entity.CustomHeadersJson = request.CustomHeadersJson ?? entity.CustomHeadersJson;
        entity.RateLimitPerMinute = request.RateLimitPerMinute ?? entity.RateLimitPerMinute;
        entity.MaxConcurrency = request.MaxConcurrency ?? entity.MaxConcurrency;
        entity.TimeoutSeconds = request.TimeoutSeconds ?? entity.TimeoutSeconds;
        entity.MaxAttempts = request.MaxAttempts ?? entity.MaxAttempts;
        entity.DeliveryWindowHours = request.DeliveryWindowHours ?? entity.DeliveryWindowHours;
        entity.BackoffLadderJson = request.BackoffLadderJson ?? entity.BackoffLadderJson;
        entity.BreakerFailureThreshold = request.BreakerFailureThreshold ?? entity.BreakerFailureThreshold;
        entity.BreakerOpenSeconds = request.BreakerOpenSeconds ?? entity.BreakerOpenSeconds;

        // Regla dura: ausencia del campo = conservar el secreto actual.
        if (request.AuthConfig is { ValueKind: not JsonValueKind.Null } authJson
            && entity.AuthType != OutboundAuthType.None)
        {
            entity.AuthConfig = codec.Encode(authJson, entity.AuthType);
        }
    }
}
