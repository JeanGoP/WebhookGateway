namespace WebhookGateway.Core.Notifications;

/// <summary>Elemento reclamado de dbo.NotificationOutbox para procesar por el worker.</summary>
public sealed record ClaimedNotification(
    long Id,
    byte ChannelType,
    string Recipient,
    int? IntegrationId,
    int? OutboundEndpointId,
    long? DeliveryId,
    byte AlertType,
    string Subject,
    string MetadataJson,
    short AttemptCount);

/// <summary>Información técnica de un incidente para renderizar en el correo de alerta.</summary>
public sealed record IncidentDetails(
    string IntegrationName,
    string EndpointName,
    string TargetUrl,
    short? StatusCode,
    string? ErrorSummary,
    short AttemptCount,
    DateTime OccurredAtUtc,
    string DashboardUrl);
