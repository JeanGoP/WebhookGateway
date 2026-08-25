using Microsoft.EntityFrameworkCore;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Data.Configuration;

/// <summary>
/// Resuelve un endpoint de entrada por su URL pública, con lo que la recepción necesita
/// para autenticar y hacer fanout, en una sola consulta de solo lectura.
/// </summary>
public sealed class InboundEndpointLookup(GatewayDbContext db)
{
    public Task<InboundEndpointView?> FindAsync(string integrationSlug, string endpointSlug, CancellationToken cancellationToken) =>
        db.InboundEndpoints
            .Where(e => e.Slug == endpointSlug && e.Integration!.Slug == integrationSlug)
            .Select(e => new InboundEndpointView(
                e.Id,
                e.IsActive && e.Integration!.IsActive,
                e.AuthType,
                e.AuthConfigCipher,
                e.AuthConfigKeyVersion,
                e.DedupeStrategy,
                e.DedupeSource,
                e.MaxBodyBytes,
                e.Subscriptions
                    .Where(s => s.IsActive && s.OutboundEndpoint!.IsActive)
                    .Select(s => new SubscriptionTarget(s.OutboundEndpointId, s.OutboundEndpoint!.DeliveryWindowHours))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>Proyección de solo lectura: exactamente lo que necesita la recepción, nada más.</summary>
public sealed record InboundEndpointView(
    int Id,
    bool IsActive,
    InboundAuthType AuthType,
    byte[] AuthConfigCipher,
    int AuthConfigKeyVersion,
    DedupeStrategy DedupeStrategy,
    string? DedupeSource,
    int MaxBodyBytes,
    IReadOnlyList<SubscriptionTarget> Subscriptions);

public sealed record SubscriptionTarget(int OutboundEndpointId, int DeliveryWindowHours);
