namespace WebhookGateway.Core.Domain;

/// <summary>
/// Estado de salud persistido de un endpoint de salida. Mapea dbo.EndpointHealthState.
/// </summary>
public sealed class EndpointHealthState
{
    public int OutboundEndpointId { get; set; }

    public byte HealthStatus { get; set; }

    public int ConsecutiveFailures { get; set; }

    public int ConsecutiveSuccesses { get; set; }

    public DateTime LastTransitionAt { get; set; }

    public DateTime? LastAlertSentAt { get; set; }

    public string? LastErrorMessage { get; set; }

    public short? LastStatusCode { get; set; }

    public OutboundEndpoint? OutboundEndpoint { get; set; }
}
