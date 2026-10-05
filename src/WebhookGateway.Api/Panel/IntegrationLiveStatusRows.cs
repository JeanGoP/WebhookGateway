namespace WebhookGateway.Api.Panel;

/// <summary>Las filas que devuelve el SQL del monitor y la traducción de sus códigos a texto.</summary>
public static partial class IntegrationLiveStatusEndpoints
{
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

    /*
        Los tipos tienen que ser EXACTAMENTE los de las columnas: Dapper construye estos records por su
        constructor posicional y no ensancha tipos. LastStatusCode y AttemptCount son smallint, y con
        int el monitor respondía 500 en cuanto una integración tenía un destino (nunca se había
        abierto en el navegador; se vio el 2026-10-05). La conversión a int va en el DTO.
    */
    private sealed record IntegrationHeaderRow(int Id, string Name, string Slug, bool IsActive, DateTime CreatedAt);
    private sealed record StatusCountRow(byte Status, long Total);
    private sealed record EndpointStatusCountRow(int OutboundEndpointId, byte Status, long Total);
    private sealed record EndpointLagRow(int OutboundEndpointId, int LagSeconds);
    private sealed record OutboundHealthRow(
        int Id, string Name, string TargetUrl, int RateLimitPerMinute, int MaxConcurrency,
        bool IsActive, byte HealthStatus, int ConsecutiveFailures,
        short? LastStatusCode, string? LastErrorMessage, DateTime? LastTransitionAt);
    private sealed record TrafficSummaryRow(
        long TotalMessages, long DuplicateMessages, long NoSubscriptionMessages);
    private sealed record RecentDeliveryRow(
        long Id, DateTime CreatedAt, int OutboundEndpointId, string OutboundName,
        byte Status, short AttemptCount, short? LastStatusCode, string? LastError);
}
