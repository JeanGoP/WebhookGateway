using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Traffic;

namespace WebhookGateway.Api.Panel;

/*
    Las consultas del explorador viven en Data y devuelven `Status` como el byte que hay
    en SQL. Aquí se reexponen como enum: el documento OpenAPI declara entonces los nombres
    de estado y el frontend recibe una unión de literales en vez de un número suelto que
    habría que volver a mapear a mano al otro lado.
*/

public sealed record MessageSummaryDto(
    long Id,
    DateTime ReceivedAt,
    int InboundEndpointId,
    string SourceIp,
    string HttpMethod,
    int BodySizeBytes,
    MessageStatus Status,
    string EndpointName,
    string IntegrationName);

public sealed record MessageDetailDto(
    long Id,
    DateTime ReceivedAt,
    int InboundEndpointId,
    string SourceIp,
    string HttpMethod,
    string? HeadersJson,
    string? QueryString,
    int BodySizeBytes,
    MessageStatus Status,
    string EndpointName,
    string IntegrationName);

public sealed record DeliveryDto(
    long Id,
    DateTime CreatedAt,
    int OutboundEndpointId,
    DeliveryStatus Status,
    short AttemptCount,
    DateTime NextAttemptAt,
    DateTime ExpiresAt,
    short? LastStatusCode,
    string? LastError,
    DateTime? CompletedAt,
    string EndpointName,
    string TargetUrl);

public sealed record AttemptDto(
    long Id,
    DateTime StartedAt,
    short AttemptNumber,
    int DurationMs,
    short? StatusCode,
    string? ResponseHeadersJson,
    string? ResponseBody,
    string? ErrorMessage,
    string? WorkerId);

internal static class MessageDtoExtensions
{
    internal static MessageSummaryDto ToDto(this MessageSummary m) => new(
        m.Id, m.ReceivedAt, m.InboundEndpointId, m.SourceIp, m.HttpMethod,
        m.BodySizeBytes, (MessageStatus)m.Status, m.EndpointName, m.IntegrationName);

    internal static MessageDetailDto ToDto(this MessageDetail m) => new(
        m.Id, m.ReceivedAt, m.InboundEndpointId, m.SourceIp, m.HttpMethod,
        m.HeadersJson, m.QueryString, m.BodySizeBytes, (MessageStatus)m.Status,
        m.EndpointName, m.IntegrationName);

    internal static DeliveryDto ToDto(this DeliverySummary d) => new(
        d.Id, d.CreatedAt, d.OutboundEndpointId, (DeliveryStatus)d.Status,
        d.AttemptCount, d.NextAttemptAt, d.ExpiresAt,
        d.LastStatusCode, d.LastError, d.CompletedAt, d.EndpointName, d.TargetUrl);

    internal static AttemptDto ToDto(this AttemptDetail a) => new(
        a.Id, a.StartedAt, a.AttemptNumber, a.DurationMs, a.StatusCode,
        a.ResponseHeadersJson, a.ResponseBody, a.ErrorMessage, a.WorkerId);
}
