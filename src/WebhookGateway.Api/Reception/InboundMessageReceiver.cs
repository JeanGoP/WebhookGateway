using System.Security.Cryptography;
using System.Text.Json;
using WebhookGateway.Core.Abstractions;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Common;
using WebhookGateway.Core.Domain;
using WebhookGateway.Core.Reception;
using WebhookGateway.Data.Configuration;
using WebhookGateway.Data.Security;
using WebhookGateway.Data.Traffic;

namespace WebhookGateway.Api.Reception;

/// <summary>
/// El caso de uso completo de recibir un webhook: localizar el endpoint, autenticar,
/// deduplicar, guardar y crear una entrega por cada destino suscrito.
/// </summary>
public sealed class InboundMessageReceiver(
    InboundEndpointLookup lookup,
    AuthConfigCodec authCodec,
    IEnumerable<IInboundAuthValidator> validators,
    IPayloadStore payloadStore,
    TrafficWriter trafficWriter,
    IDeliveryQueue deliveryQueue,
    TimeProvider clock)
{
    // Ningún proveedor real reintenta un webhook más allá de unos días; una semana sobra
    // como ventana para detectar reenvíos y mantiene la tabla de deduplicación pequeña.
    private static readonly TimeSpan DedupeRetention = TimeSpan.FromDays(7);

    public async Task<Result<ReceiveOutcome>> ReceiveAsync(
        string integrationSlug, string endpointSlug, InboundRequest request, CancellationToken cancellationToken)
    {
        var endpoint = await lookup.FindAsync(integrationSlug, endpointSlug, cancellationToken);

        if (endpoint is null || !endpoint.IsActive)
        {
            return Result.Fail<ReceiveOutcome>("reception.not_found", "El endpoint no existe o está inactivo.");
        }

        if (request.Body.Length > endpoint.MaxBodyBytes)
        {
            return Result.Fail<ReceiveOutcome>(
                "reception.body_too_large", $"El cuerpo supera el máximo de {endpoint.MaxBodyBytes} bytes de este endpoint.");
        }

        var authResult = await AuthenticateAsync(endpoint, request, cancellationToken);
        if (authResult.IsFailure)
        {
            return Result.Fail<ReceiveOutcome>(authResult.Error);
        }

        var dedupeKey = DedupeKeyExtractor.Extract(endpoint.DedupeStrategy, endpoint.DedupeSource, request.Headers, request.Body.Span);

        // El id real del mensaje aún no existe (lo asigna el IDENTITY al insertarlo). Para
        // el almacenamiento inline no hace falta: solo lo necesitará una implementación
        // externa de IPayloadStore (F5), que tendrá que resolver esto con una clave propia
        // en vez de depender del id de SQL.
        var payload = await payloadStore.SaveAsync(0, request.Body, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;

        var writeResult = await trafficWriter.WriteAsync(
            new TrafficWriteRequest(
                endpoint.Id,
                now,
                request.SourceIp,
                request.Method,
                JsonSerializer.Serialize(HeaderMasking.Mask(request.Headers)),
                request.Query.Count == 0 ? null : string.Join('&', request.Query.Select(q => $"{q.Key}={q.Value}")),
                request.Body.Length,
                SHA256.HashData(request.Body.Span),
                dedupeKey,
                now.Add(DedupeRetention),
                payload,
                endpoint.Subscriptions.Select(s => new DeliveryTarget(s.OutboundEndpointId, s.DeliveryWindowHours)).ToList()),
            cancellationToken);

        if (writeResult.DeliveryIds.Count > 0)
        {
            deliveryQueue.TryEnqueue(writeResult.DeliveryIds);
        }

        return Result.Ok(new ReceiveOutcome(writeResult.MessageId, writeResult.Status, writeResult.DeliveryIds.Count));
    }

    private async Task<Result> AuthenticateAsync(InboundEndpointView endpoint, InboundRequest request, CancellationToken cancellationToken)
    {
        if (endpoint.AuthType == InboundAuthType.None)
        {
            return Result.Ok();
        }

        var validator = validators.FirstOrDefault(v => v.Type == endpoint.AuthType)
            ?? throw new InvalidOperationException($"No hay validador registrado para {endpoint.AuthType}.");

        var config = authCodec.Decode(new ProtectedSecret(endpoint.AuthConfigCipher, endpoint.AuthConfigKeyVersion), endpoint.AuthType);
        return await validator.ValidateAsync(request, config, cancellationToken);
    }
}

public sealed record ReceiveOutcome(long MessageId, MessageStatus Status, int DeliveryCount);
