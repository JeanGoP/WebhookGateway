namespace WebhookGateway.Api.Panel;

/// <summary>Lo que devuelve <c>/api/integrations/{id}/live-status</c>.</summary>
/// <param name="LagAlertSeconds">
/// Retraso a partir del cual un destino se considera atrasado (<c>Gateway:Monitoring:MaxLagMinutes</c>).
/// </param>
public sealed record IntegrationLiveStatusDto(
    int IntegrationId, string Name, string Slug, bool IsActive,
    QueueCountsDto Queue, IReadOnlyList<OutboundHealthItemDto> OutboundEndpoints,
    TrafficSummaryDto Traffic24h, IReadOnlyList<RecentDeliveryFeedDto> RecentDeliveries,
    int LagAlertSeconds);

/// <summary>
/// Conteos de la cola. <see cref="Pending"/>, <see cref="InFlight"/> y <see cref="Retrying"/> son el
/// total real, sin acotar por fecha: una entrega puede estar legítimamente pendiente tres días, que
/// es su ventana de entrega. Los cuatro estados terminales son de las últimas
/// <see cref="TerminalWindowHours"/> horas, porque esos crecen para siempre y contarlos enteros en un
/// monitor que sondea cada segundo recorrería la tabla completa.
/// </summary>
public sealed record QueueCountsDto(
    long Pending, long InFlight, long Retrying, long Delivered,
    long Failed, long Expired, long Cancelled, long TotalActiveQueue,
    int TerminalWindowHours);

/// <param name="ActiveDeliveries">Pendientes, en vuelo y reintentando de este destino.</param>
/// <param name="LagSeconds">
/// Cuánto lleva esperando su entrega vencida más antigua; nulo si no tiene ninguna esperando. Dice si
/// el destino va al día mejor que el tamaño de su cola: una cola grande que se vacía a tiempo no es
/// un problema, y una pequeña que no avanza sí.
/// </param>
public sealed record OutboundHealthItemDto(
    int Id, string Name, string TargetUrl, int RateLimitPerMinute, int MaxConcurrency,
    bool IsActive, string HealthStatus, int ConsecutiveFailures,
    int? LastStatusCode, string? LastErrorMessage, DateTime? LastTransitionAt,
    long ActiveDeliveries, int? LagSeconds);

/// <summary>
/// Mensajes recibidos en la ventana. <see cref="DuplicateMessages"/> son los que coincidieron con una
/// clave de deduplicación ya vista; <see cref="NoSubscriptionMessages"/> son los que llegaron bien
/// pero no tenían ninguna suscripción activa que los reclamara. Son problemas distintos: el primero
/// es un emisor que reenvía, el segundo una configuración a medias.
/// </summary>
public sealed record TrafficSummaryDto(
    long TotalMessages, long DuplicateMessages, long NoSubscriptionMessages);

public sealed record RecentDeliveryFeedDto(
    long Id, DateTime CreatedAt, int OutboundEndpointId, string OutboundName,
    string Status, int AttemptCount, int? LastStatusCode, string? LastError);
