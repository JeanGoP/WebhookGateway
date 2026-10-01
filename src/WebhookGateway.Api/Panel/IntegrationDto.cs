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
    DateTime CreatedAt,
    string HealthStatus = "Healthy",
    int TotalOutbound = 0,
    int HealthyOutbound = 0,
    int DegradedOutbound = 0,
    int DownOutbound = 0);

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
    int ActiveSubscriptions,
    string HealthStatus = "Healthy",
    int TotalOutbound = 0,
    int HealthyOutbound = 0,
    int DegradedOutbound = 0,
    int DownOutbound = 0);

internal static class IntegrationDtoExtensions
{
    internal static IntegrationDto ToListDto(
        this Integration i,
        string healthStatus = "Healthy",
        int totalOutbound = 0,
        int healthyOutbound = 0,
        int degradedOutbound = 0,
        int downOutbound = 0) => new(
        i.Id, i.Name, i.Slug, i.Description, i.IsActive,
        i.RetentionDays, i.PayloadRetentionDays, i.CreatedAt,
        healthStatus, totalOutbound, healthyOutbound, degradedOutbound, downOutbound);

    internal static IntegrationDetailDto ToDetailDto(
        this Integration i,
        int activeInbound,
        int activeOutbound,
        int activeSubscriptions,
        string healthStatus = "Healthy",
        int totalOutbound = 0,
        int healthyOutbound = 0,
        int degradedOutbound = 0,
        int downOutbound = 0) => new(
        i.Id, i.Name, i.Slug, i.Description, i.IsActive,
        i.RetentionDays, i.PayloadRetentionDays, i.CreatedAt,
        activeInbound, activeOutbound, activeSubscriptions,
        healthStatus, totalOutbound, healthyOutbound, degradedOutbound, downOutbound);
}
