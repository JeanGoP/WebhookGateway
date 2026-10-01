using Dapper;
using WebhookGateway.Data.Db;

namespace WebhookGateway.Api.Panel;

/// <summary>
/// Monitor en tiempo real para una integración: <c>/api/integrations/{id}/live-status</c>.
/// Diseñado para sondeo continuo de alta frecuencia (1s a 15s) con 1 solo roundtrip y WITH (NOLOCK).
/// </summary>
public static class IntegrationLiveStatusEndpoints
{
    /// <summary>
    /// Ventana de los conteos históricos. La cola activa no se acota: ver el comentario del SQL.
    /// </summary>
    private const int WindowHours = 24;

    private const string LiveStatusSql = """
        SELECT Id, Name, Slug, IsActive, CreatedAt
        FROM dbo.Integration WITH (NOLOCK)
        WHERE Id = @IntegrationId;

        /*
            Los conteos por estado, en dos partes a propósito.

            Antes era un solo GROUP BY sin filtro de fecha: contaba TODO el histórico de
            WebhookDelivery en cada sondeo, y el panel puede sondear cada segundo. Con quince
            millones de filas al año eso es un recorrido completo por segundo.

            Y no se arregla con un filtro de 24 h a todo: la cola activa no se puede acotar por
            fecha. La ventana de entrega por defecto son 72 horas, así que una entrega legítimamente
            pendiente puede tener tres días; con un corte de 24 h, un destino caído desde anteayer
            mostraría la cola vacía, que es justo lo contrario de lo que un monitor tiene que decir.

            Así que: la cola activa (0, 1, 2) va completa y sale de índices filtrados por estado, que
            solo contienen lo pendiente. Lo terminal (3 a 6), que es lo que crece para siempre, se
            acota a 24 horas.
        */
        SELECT Status, COUNT_BIG(1) AS Total
        FROM dbo.WebhookDelivery WITH (NOLOCK)
        WHERE OutboundEndpointId IN (
            SELECT Id FROM dbo.OutboundEndpoint WITH (NOLOCK) WHERE IntegrationId = @IntegrationId
        )
          AND Status IN (0, 1, 2)
        GROUP BY Status;

        SELECT Status, COUNT_BIG(1) AS Total
        FROM dbo.WebhookDelivery WITH (NOLOCK)
        WHERE OutboundEndpointId IN (
            SELECT Id FROM dbo.OutboundEndpoint WITH (NOLOCK) WHERE IntegrationId = @IntegrationId
        )
          AND Status NOT IN (0, 1, 2)
          AND CreatedAt >= DATEADD(HOUR, -@WindowHours, SYSUTCDATETIME())
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

        /*
            Status 1 es Duplicate y 2 es NoSubscriptions (ver MessageStatus). Esto contaba los 2 como
            duplicados, así que el panel llamaba "duplicado" a un mensaje que en realidad llegó bien
            pero no tenía ninguna suscripción activa que lo reclamara: dos problemas opuestos
            —configuración olvidada frente a emisor que reenvía— mostrados como el mismo.
        */
        SELECT
            COUNT_BIG(1) AS TotalMessages,
            COUNT_BIG(CASE WHEN m.Status = 1 THEN 1 END) AS DuplicateMessages,
            COUNT_BIG(CASE WHEN m.Status = 2 THEN 1 END) AS NoSubscriptionMessages
        FROM dbo.WebhookMessage m WITH (NOLOCK)
        WHERE m.InboundEndpointId IN (
            SELECT Id FROM dbo.InboundEndpoint WITH (NOLOCK) WHERE IntegrationId = @IntegrationId
        )
        AND m.ReceivedAt >= DATEADD(HOUR, -@WindowHours, SYSUTCDATETIME());

        -- Acotado a la misma ventana: sin fecha, una integración sin entregas recorría la tabla
        -- entera hacia atrás buscando diez filas que no existen.
        SELECT TOP (10)
            d.Id, d.CreatedAt, d.OutboundEndpointId, o.Name AS OutboundName,
            d.Status, d.AttemptCount, d.LastStatusCode, d.LastError
        FROM dbo.WebhookDelivery d WITH (NOLOCK)
        JOIN dbo.OutboundEndpoint o WITH (NOLOCK) ON o.Id = d.OutboundEndpointId
        WHERE o.IntegrationId = @IntegrationId
          AND d.CreatedAt >= DATEADD(HOUR, -@WindowHours, SYSUTCDATETIME())
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
            new CommandDefinition(
                LiveStatusSql, new { IntegrationId = id, WindowHours }, cancellationToken: ct));

        var integration = await multi.ReadFirstOrDefaultAsync<IntegrationHeaderRow>();
        if (integration is null)
        {
            return Results.NotFound(new ErrorResponse("Integración no encontrada."));
        }

        var activeRows = (await multi.ReadAsync<StatusCountRow>()).ToDictionary(r => r.Status, r => r.Total);
        var terminalRows = (await multi.ReadAsync<StatusCountRow>()).ToDictionary(r => r.Status, r => r.Total);
        var outboundRows = await multi.ReadAsync<OutboundHealthRow>();
        var trafficRow = await multi.ReadFirstOrDefaultAsync<TrafficSummaryRow>();
        var recentRows = await multi.ReadAsync<RecentDeliveryRow>();

        // La cola activa es el total real. Lo terminal es de las últimas WindowHours.
        var pending = activeRows.GetValueOrDefault((byte)0, 0);
        var inFlight = activeRows.GetValueOrDefault((byte)1, 0);
        var retrying = activeRows.GetValueOrDefault((byte)2, 0);
        var delivered = terminalRows.GetValueOrDefault((byte)3, 0);
        var failed = terminalRows.GetValueOrDefault((byte)4, 0);
        var expired = terminalRows.GetValueOrDefault((byte)5, 0);
        var cancelled = terminalRows.GetValueOrDefault((byte)6, 0);

        var queue = new QueueCountsDto(
            pending, inFlight, retrying, delivered, failed, expired, cancelled,
            pending + inFlight + retrying, WindowHours);

        var outbound = outboundRows.Select(o => new OutboundHealthItemDto(
            o.Id, o.Name, o.TargetUrl, o.RateLimitPerMinute, o.MaxConcurrency, o.IsActive,
            MapHealthStatus(o.HealthStatus), o.ConsecutiveFailures,
            o.LastStatusCode, o.LastErrorMessage, o.LastTransitionAt)).ToList();

        var traffic = new TrafficSummaryDto(
            trafficRow?.TotalMessages ?? 0,
            trafficRow?.DuplicateMessages ?? 0,
            trafficRow?.NoSubscriptionMessages ?? 0);

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
    private sealed record TrafficSummaryRow(
        long TotalMessages, long DuplicateMessages, long NoSubscriptionMessages);
    private sealed record RecentDeliveryRow(
        long Id, DateTime CreatedAt, int OutboundEndpointId, string OutboundName,
        byte Status, int AttemptCount, int? LastStatusCode, string? LastError);
}
