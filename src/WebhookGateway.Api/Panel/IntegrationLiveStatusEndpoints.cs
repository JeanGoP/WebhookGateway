using Dapper;
using WebhookGateway.Data.Db;

namespace WebhookGateway.Api.Panel;

/// <summary>
/// Monitor en tiempo real para una integración: <c>/api/integrations/{id}/live-status</c>.
/// Diseñado para sondeo continuo de alta frecuencia (1s a 15s) con 1 solo roundtrip y WITH (NOLOCK).
/// </summary>
public static class IntegrationLiveStatusEndpoints
{
    private const string LiveStatusSql = """
        SELECT Id, Name, Slug, IsActive, CreatedAt
        FROM dbo.Integration WITH (NOLOCK)
        WHERE Id = @IntegrationId;

        SELECT Status, COUNT_BIG(1) AS Total
        FROM dbo.WebhookDelivery WITH (NOLOCK)
        WHERE OutboundEndpointId IN (
            SELECT Id FROM dbo.OutboundEndpoint WITH (NOLOCK) WHERE IntegrationId = @IntegrationId
        )
        GROUP BY Status;

        SELECT 
            o.Id, o.Name, o.TargetUrl, o.RateLimitPerMinute, o.MaxConcurrency, o.IsActive,
            ISNULL(h.HealthStatus, 0) AS HealthStatus,
            ISNULL(h.ConsecutiveFailures, 0) AS ConsecutiveFailures,
            h.LastStatusCode, h.LastErrorMessage, h.LastTransitionAt
        FROM dbo.OutboundEndpoint o WITH (NOLOCK)
        LEFT JOIN dbo.EndpointHealthState h WITH (NOLOCK) ON h.OutboundEndpointId = o.Id
        WHERE o.IntegrationId = @IntegrationId
        ORDER BY o.Name;

        SELECT 
            COUNT_BIG(1) AS TotalMessages,
            COUNT_BIG(CASE WHEN m.Status = 2 THEN 1 END) AS DuplicateMessages
        FROM dbo.WebhookMessage m WITH (NOLOCK)
        WHERE m.InboundEndpointId IN (
            SELECT Id FROM dbo.InboundEndpoint WITH (NOLOCK) WHERE IntegrationId = @IntegrationId
        )
        AND m.ReceivedAt >= DATEADD(HOUR, -24, SYSUTCDATETIME());

        SELECT TOP (10)
            d.Id, d.CreatedAt, d.OutboundEndpointId, o.Name AS OutboundName,
            d.Status, d.AttemptCount, d.LastStatusCode, d.LastError
        FROM dbo.WebhookDelivery d WITH (NOLOCK)
        JOIN dbo.OutboundEndpoint o WITH (NOLOCK) ON o.Id = d.OutboundEndpointId
        WHERE o.IntegrationId = @IntegrationId
        ORDER BY d.CreatedAt DESC, d.Id DESC;
        """;

    public static void MapIntegrationLiveStatus(this WebApplication app)
    {
        app.MapGroup("/api/integrations")
            .WithTags("Integrations")
            .RequireAuthorization()
            .MapGet("/{id:int}/live-status", GetLiveStatusAsync)
            .Produces<IntegrationLiveStatusDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetLiveStatusAsync(
        int id, ISqlConnectionFactory connectionFactory, CancellationToken ct)
    {
        using var connection = await connectionFactory.OpenAsync(ct);
        using var multi = await connection.QueryMultipleAsync(
            new CommandDefinition(LiveStatusSql, new { IntegrationId = id }, cancellationToken: ct));

        var integration = await multi.ReadFirstOrDefaultAsync<IntegrationHeaderRow>();
        if (integration is null)
        {
            return Results.NotFound(new ErrorResponse("Integración no encontrada."));
        }

        var queueRows = (await multi.ReadAsync<StatusCountRow>()).ToDictionary(r => r.Status, r => r.Total);
        var outboundRows = await multi.ReadAsync<OutboundHealthRow>();
        var trafficRow = await multi.ReadFirstOrDefaultAsync<TrafficSummaryRow>();
        var recentRows = await multi.ReadAsync<RecentDeliveryRow>();

        var pending = queueRows.GetValueOrDefault((byte)0, 0);
        var inFlight = queueRows.GetValueOrDefault((byte)1, 0);
        var retrying = queueRows.GetValueOrDefault((byte)2, 0);
        var delivered = queueRows.GetValueOrDefault((byte)3, 0);
        var failed = queueRows.GetValueOrDefault((byte)4, 0);
        var expired = queueRows.GetValueOrDefault((byte)5, 0);
        var cancelled = queueRows.GetValueOrDefault((byte)6, 0);

        var queue = new QueueCountsDto(
            pending, inFlight, retrying, delivered, failed, expired, cancelled,
            pending + inFlight + retrying);

        var outbound = outboundRows.Select(o => new OutboundHealthItemDto(
            o.Id, o.Name, o.TargetUrl, o.RateLimitPerMinute, o.MaxConcurrency, o.IsActive,
            MapHealthStatus(o.HealthStatus), o.ConsecutiveFailures,
            o.LastStatusCode, o.LastErrorMessage, o.LastTransitionAt)).ToList();

        var traffic = new TrafficSummaryDto(trafficRow?.TotalMessages ?? 0, trafficRow?.DuplicateMessages ?? 0);

        var recent = recentRows.Select(r => new RecentDeliveryFeedDto(
            r.Id, r.CreatedAt, r.OutboundEndpointId, r.OutboundName,
            MapDeliveryStatus(r.Status), r.AttemptCount, r.LastStatusCode, r.LastError)).ToList();

        return Results.Ok(new IntegrationLiveStatusDto(
            integration.Id, integration.Name, integration.Slug, integration.IsActive,
            queue, outbound, traffic, recent));
    }

    private static string MapHealthStatus(byte status) => status switch
    {
        1 => "Degraded",
        2 => "Down",
        3 => "Recovered",
        _ => "Healthy"
    };

    private static string MapDeliveryStatus(byte status) => status switch
    {
        1 => "InFlight",
        2 => "Retrying",
        3 => "Delivered",
        4 => "Failed",
        5 => "Expired",
        6 => "Cancelled",
        _ => "Pending"
    };

    private sealed record IntegrationHeaderRow(int Id, string Name, string Slug, bool IsActive, DateTime CreatedAt);
    private sealed record StatusCountRow(byte Status, long Total);
    private sealed record OutboundHealthRow(
        int Id, string Name, string TargetUrl, int RateLimitPerMinute, int MaxConcurrency,
        bool IsActive, byte HealthStatus, int ConsecutiveFailures,
        int? LastStatusCode, string? LastErrorMessage, DateTime? LastTransitionAt);
    private sealed record TrafficSummaryRow(long TotalMessages, long DuplicateMessages);
    private sealed record RecentDeliveryRow(
        long Id, DateTime CreatedAt, int OutboundEndpointId, string OutboundName,
        byte Status, int AttemptCount, int? LastStatusCode, string? LastError);
}

public sealed record IntegrationLiveStatusDto(
    int IntegrationId, string Name, string Slug, bool IsActive,
    QueueCountsDto Queue, IReadOnlyList<OutboundHealthItemDto> OutboundEndpoints,
    TrafficSummaryDto Traffic24h, IReadOnlyList<RecentDeliveryFeedDto> RecentDeliveries);

public sealed record QueueCountsDto(
    long Pending, long InFlight, long Retrying, long Delivered,
    long Failed, long Expired, long Cancelled, long TotalActiveQueue);

public sealed record OutboundHealthItemDto(
    int Id, string Name, string TargetUrl, int RateLimitPerMinute, int MaxConcurrency,
    bool IsActive, string HealthStatus, int ConsecutiveFailures,
    int? LastStatusCode, string? LastErrorMessage, DateTime? LastTransitionAt);

public sealed record TrafficSummaryDto(long TotalMessages, long DuplicateMessages);

public sealed record RecentDeliveryFeedDto(
    long Id, DateTime CreatedAt, int OutboundEndpointId, string OutboundName,
    string Status, int AttemptCount, int? LastStatusCode, string? LastError);
