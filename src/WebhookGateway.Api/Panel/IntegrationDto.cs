using WebhookGateway.Core.Domain;

namespace WebhookGateway.Api.Panel;

public sealed record IntegrationRequest(
    string? Name,
    string? Slug,
    string? Description,
    bool? IsActive,
    int? RetentionDays,
    int? PayloadRetentionDays);

public sealed record IntegrationDto(
    int Id,
    string Name,
    string Slug,
    string? Description,
    bool IsActive,
    int RetentionDays,
    int PayloadRetentionDays,
    DateTime CreatedAt);

/// <param name="ActiveSubscriptions">Suscripciones activas colgando de sus endpoints de entrada.</param>
public sealed record IntegrationDetailDto(
    int Id,
    string Name,
    string Slug,
    string? Description,
    bool IsActive,
    int RetentionDays,
    int PayloadRetentionDays,
    DateTime CreatedAt,
    int ActiveInbound,
    int ActiveOutbound,
    int ActiveSubscriptions);

internal static class IntegrationDtoExtensions
{
    internal static IntegrationDto ToListDto(this Integration i) => new(
        i.Id, i.Name, i.Slug, i.Description, i.IsActive,
        i.RetentionDays, i.PayloadRetentionDays, i.CreatedAt);

    internal static IntegrationDetailDto ToDetailDto(
        this Integration i, int activeInbound, int activeOutbound, int activeSubscriptions) => new(
        i.Id, i.Name, i.Slug, i.Description, i.IsActive,
        i.RetentionDays, i.PayloadRetentionDays, i.CreatedAt,
        activeInbound, activeOutbound, activeSubscriptions);
}
