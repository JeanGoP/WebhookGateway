using WebhookGateway.Core.Domain;

namespace WebhookGateway.Api.Panel;

public sealed record SubscriptionRequest(int? InboundEndpointId, int? OutboundEndpointId);

public sealed record SubscriptionUpdateRequest(bool? IsActive);

/// <param name="InboundName">
/// Nulo cuando la suscripción se devuelve sin sus navegaciones cargadas, como en el
/// <c>PUT</c>, donde solo se toca <c>IsActive</c>.
/// </param>
public sealed record SubscriptionDto(
    int Id,
    int InboundEndpointId,
    int OutboundEndpointId,
    bool IsActive,
    DateTime CreatedAt,
    string? InboundName,
    string? OutboundName);

internal static class SubscriptionDtoExtensions
{
    internal static SubscriptionDto ToDto(this Subscription s) => new(
        s.Id, s.InboundEndpointId, s.OutboundEndpointId, s.IsActive, s.CreatedAt,
        s.InboundEndpoint?.Name,
        s.OutboundEndpoint?.Name);
}
