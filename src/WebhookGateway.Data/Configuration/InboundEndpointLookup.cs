using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Data.Configuration;

/// <summary>
/// Resuelve un endpoint de entrada por su URL pública, con lo que la recepción necesita
/// para autenticar y hacer fanout. Usa caché en memoria para no consultar SQL en cada
/// petición HTTP bajo alta concurrencia.
/// </summary>
public sealed class InboundEndpointLookup(GatewayDbContext db, IMemoryCache cache, InboundConfigVersion version)
{
    private static readonly TimeSpan HitDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Las rutas que no existen también se cachean, pero poco. Sin esto, cada petición a una ruta
    /// inexistente va a SQL: un escáner o un emisor mal configurado pega directo a la base, que es
    /// la misma que atiende las recepciones buenas. Y poco tiempo porque la ruta que hoy no existe
    /// puede crearse en el panel dentro de un minuto.
    /// </summary>
    private static readonly TimeSpan MissDuration = TimeSpan.FromSeconds(30);

    public async Task<InboundEndpointView?> FindAsync(string integrationSlug, string endpointSlug, CancellationToken cancellationToken)
    {
        var cacheKey =
            $"inbound_endpoint:{version.Current}:{integrationSlug.ToLowerInvariant()}:{endpointSlug.ToLowerInvariant()}";

        // El envoltorio no es ceremonia: hay que distinguir "ya sabemos que esta ruta no existe" de
        // "no está en la caché", y guardar un nulo pelado deja esa distinción en manos de cómo trate
        // IMemoryCache los valores nulos. Con una entrada siempre no nula, la lectura es inequívoca.
        if (cache.TryGetValue(cacheKey, out CachedLookup? cached) && cached is not null)
        {
            return cached.Endpoint;
        }

        var endpoint = await db.InboundEndpoints
            .Where(e => e.Slug == endpointSlug && e.Integration!.Slug == integrationSlug)
            .Select(e => new InboundEndpointView(
                e.Id,
                e.IntegrationId,
                e.Integration!.Name,
                e.Name,
                e.IsActive && e.Integration!.IsActive,
                e.AuthType,
                e.AuthConfigCipher,
                e.AuthConfigKeyVersion,
                e.DedupeStrategy,
                e.DedupeSource,
                e.MaxBodyBytes,
                e.TransientFailureStatusCode,
                e.Subscriptions
                    .Where(s => s.IsActive && s.OutboundEndpoint!.IsActive)
                    .Select(s => new SubscriptionTarget(s.OutboundEndpointId, s.OutboundEndpoint!.DeliveryWindowHours))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken);

        cache.Set(cacheKey, new CachedLookup(endpoint), endpoint is null ? MissDuration : HitDuration);

        if (endpoint is not null)
        {
            cache.Set(LastKnownKey(integrationSlug, endpointSlug), endpoint);
        }

        return endpoint;
    }

    /// <summary>
    /// La última configuración leída de este endpoint, aunque haya vencido. Solo memoria: no toca
    /// SQL, así que sirve justo cuando SQL no responde.
    /// </summary>
    /// <remarks>
    /// Para eso existe: al rechazar un webhook que no se pudo guardar hay que saber qué código
    /// entiende su emisor como "reintenta", y la caché normal puede haber vencido durante la caída.
    /// Cinco minutos de SQL caído bastarían para olvidarlo y responder a GHL un 503 que da por
    /// perdido. Sin vencimiento: es una entrada por endpoint real, nunca por ruta inventada.
    /// </remarks>
    public InboundEndpointView? LastKnown(string integrationSlug, string endpointSlug) =>
        cache.TryGetValue(LastKnownKey(integrationSlug, endpointSlug), out InboundEndpointView? view) ? view : null;

    private static string LastKnownKey(string integrationSlug, string endpointSlug) =>
        $"inbound_endpoint_last:{integrationSlug.ToLowerInvariant()}:{endpointSlug.ToLowerInvariant()}";

    /// <summary>Lo que se guarda en la caché. Una ruta inexistente es <c>Endpoint = null</c>.</summary>
    private sealed record CachedLookup(InboundEndpointView? Endpoint);
}

/// <summary>Proyección de solo lectura: exactamente lo que necesita la recepción, nada más.</summary>
public sealed record InboundEndpointView(
    int Id,
    int IntegrationId,
    string IntegrationName,
    string Name,
    bool IsActive,
    InboundAuthType AuthType,
    byte[] AuthConfigCipher,
    int AuthConfigKeyVersion,
    DedupeStrategy DedupeStrategy,
    string? DedupeSource,
    int MaxBodyBytes,
    short? TransientFailureStatusCode,
    IReadOnlyList<SubscriptionTarget> Subscriptions);

public sealed record SubscriptionTarget(int OutboundEndpointId, int DeliveryWindowHours);
