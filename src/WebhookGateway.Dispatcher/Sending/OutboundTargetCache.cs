using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.Json;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Delivery;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data;
using WebhookGateway.Data.Security;

namespace WebhookGateway.Dispatcher.Sending;

/// <summary>Todo lo que el despachador necesita saber de un destino, ya resuelto y descifrado.</summary>
public sealed record OutboundTarget(
    int Id,
    Uri TargetUrl,
    HttpMethod Method,
    OutboundAuthType AuthType,
    OutboundAuthConfig AuthConfig,
    IReadOnlyDictionary<string, string> CustomHeaders,
    int RateLimitPerMinute,
    int MaxConcurrency,
    TimeSpan Timeout,
    RetryPolicy RetryPolicy,
    int BreakerFailureThreshold,
    int BreakerOpenSeconds);

/// <summary>
/// Cachea la configuración de los destinos durante unos segundos.
/// </summary>
/// <remarks>
/// Sin caché, cada entrega descifraría de nuevo su configuración de autenticación y haría
/// una consulta más contra una instancia compartida. Con una caché eterna, cambiar un
/// destino desde el panel exigiría reiniciar. Unos segundos resuelven las dos cosas.
/// </remarks>
public sealed class OutboundTargetCache(
    IServiceScopeFactory scopeFactory,
    AuthConfigCodec authCodec,
    TimeProvider clock,
    IOptions<DispatcherOptions> options)
{
    private readonly ConcurrentDictionary<int, CacheEntry> _entries = new();
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(options.Value.TargetCacheSeconds);

    /// <summary>
    /// Devuelve <see langword="null"/> si el destino ya no existe o está desactivado. En ese
    /// caso la entrega no se reintenta: no hay nada a donde mandarla.
    /// </summary>
    public async Task<OutboundTarget?> GetAsync(int endpointId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (_entries.TryGetValue(endpointId, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Target;
        }

        var target = await LoadAsync(endpointId, cancellationToken);
        _entries[endpointId] = new CacheEntry(target, now + _ttl);

        return target;
    }

    private async Task<OutboundTarget?> LoadAsync(int endpointId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var endpoint = await db.OutboundEndpoints
            .Where(e => e.Id == endpointId && e.IsActive)
            .FirstOrDefaultAsync(cancellationToken);

        if (endpoint is null)
        {
            return null;
        }

        return new OutboundTarget(
            endpoint.Id,
            new Uri(endpoint.TargetUrl),
            HttpMethod.Parse(endpoint.HttpMethod),
            endpoint.AuthType,
            authCodec.Decode(endpoint.AuthConfig, endpoint.AuthType),
            ParseHeaders(endpoint.CustomHeadersJson),
            endpoint.RateLimitPerMinute,
            Math.Max(1, endpoint.MaxConcurrency),
            TimeSpan.FromSeconds(endpoint.TimeoutSeconds),
            endpoint.ResolveRetryPolicy(ParseLadder(endpoint.BackoffLadderJson)),
            endpoint.BreakerFailureThreshold,
            endpoint.BreakerOpenSeconds);
    }

    private static Dictionary<string, string> ParseHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // Una cabecera mal escrita no puede tumbar las entregas de un destino entero.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>La escalera se guarda como una lista de segundos: <c>[5, 30, 120]</c>.</summary>
    private static IReadOnlyList<TimeSpan>? ParseLadder(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var seconds = JsonSerializer.Deserialize<double[]>(json);
            return seconds is { Length: > 0 } ? [.. seconds.Select(TimeSpan.FromSeconds)] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record CacheEntry(OutboundTarget? Target, DateTimeOffset ExpiresAt);
}
