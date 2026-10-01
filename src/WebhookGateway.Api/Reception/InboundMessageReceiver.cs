using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebhookGateway.Core.Abstractions;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Common;
using WebhookGateway.Core.Domain;
using WebhookGateway.Core.Notifications;
using WebhookGateway.Core.Reception;
using WebhookGateway.Data.Configuration;
using WebhookGateway.Data.Security;
using WebhookGateway.Data.Traffic;
using WebhookGateway.Dispatcher.Notifications;

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
    NotificationStore notifications,
    IOptions<NotificationOptions> notificationOptions,
    TimeProvider clock,
    ILogger<InboundMessageReceiver> logger)
{
    private static readonly TimeSpan DedupeRetention = TimeSpan.FromDays(7);

    public async Task<Result<ReceiveOutcome>> ReceiveAsync(
        string integrationSlug, string endpointSlug, InboundRequest request, CancellationToken cancellationToken)
    {
        var endpoint = await lookup.FindAsync(integrationSlug, endpointSlug, cancellationToken);

        if (endpoint is null || !endpoint.IsActive)
        {
            return Result.Fail<ReceiveOutcome>("reception.not_found", "El endpoint no existe o está inactivo.");
        }

        var authResult = await AuthenticateAsync(endpoint, request, cancellationToken);
        if (authResult.IsFailure)
        {
            return Result.Fail<ReceiveOutcome>(authResult.Error);
        }

        if (request.Body.Length > endpoint.MaxBodyBytes)
        {
            await TryAlertAsync(endpoint, request, 413,
                $"El cuerpo ({request.Body.Length:N0} bytes) supera el máximo de {endpoint.MaxBodyBytes:N0} bytes de este endpoint.",
                cancellationToken);

            return Result.Fail<ReceiveOutcome>(
                "reception.body_too_large", $"El cuerpo supera el máximo de {endpoint.MaxBodyBytes} bytes de este endpoint.");
        }

        if (request.Body.Length > 0 &&
            request.Headers.TryGetValue("Content-Type", out var contentType) &&
            contentType.Contains("json", StringComparison.OrdinalIgnoreCase) &&
            !IsValidJson(request.Body.Span))
        {
            await TryAlertAsync(endpoint, request, 400, "El cuerpo contiene JSON sintácticamente inválido.", cancellationToken);
            return Result.Fail<ReceiveOutcome>("reception.invalid_json", "El cuerpo contiene JSON sintácticamente inválido.");
        }

        var dedupeKey = DedupeKeyExtractor.Extract(endpoint.DedupeStrategy, endpoint.DedupeSource, request.Headers, request.Body.Span);

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

    private static bool IsValidJson(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            while (reader.Read()) { }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task TryAlertAsync(
        InboundEndpointView endpoint, InboundRequest request, int statusCode, string errorSummary, CancellationToken ct)
    {
        if (!notificationOptions.Value.Enabled)
        {
            return;
        }

        try
        {
            var subject = $"[ALERTA] Error de recepción: {endpoint.IntegrationName} / {endpoint.Name}";
            var details = new IncidentDetails(
                IntegrationName: endpoint.IntegrationName,
                EndpointName: endpoint.Name,
                TargetUrl: request.Path,
                StatusCode: (short)statusCode,
                ErrorSummary: errorSummary,
                AttemptCount: 1,
                OccurredAtUtc: clock.GetUtcNow().UtcDateTime,
                DashboardUrl: $"{notificationOptions.Value.DashboardBaseUrl}/integrations/{endpoint.IntegrationId}");

            var metadataJson = JsonSerializer.Serialize(details);
            var subscribers = await notifications.GetVerifiedSubscriberEmailsAsync(endpoint.IntegrationId, ct);
            var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(notificationOptions.Value.AdminEmail))
            {
                recipients.Add(notificationOptions.Value.AdminEmail);
            }

            foreach (var email in subscribers)
            {
                recipients.Add(email);
            }

            foreach (var recipient in recipients)
            {
                await notifications.EnqueueAsync(
                    NotificationChannel.Email, recipient, endpoint.IntegrationId, null, null,
                    NotificationAlertType.InboundError, subject, metadataJson, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error al encolar alerta inbound para integración {IntegrationId}", endpoint.IntegrationId);
        }
    }
}

public sealed record ReceiveOutcome(long MessageId, MessageStatus Status, int DeliveryCount);
