using WebhookGateway.Core.Domain;

namespace WebhookGateway.Api.Panel;

public sealed record SubscriberDto(
    int Id,
    int IntegrationId,
    string Email,
    string Status,
    DateTime CreatedAt,
    DateTime? VerifiedAt,
    string CreatedBy);

public sealed record AddSubscriberRequest(string Email);

public static class SubscriberDtoExtensions
{
    public static SubscriberDto ToDto(this IntegrationEmailSubscriber s) =>
        new(s.Id, s.IntegrationId, s.Email, s.Status.ToString(), s.CreatedAt, s.VerifiedAt, s.CreatedBy);
}
