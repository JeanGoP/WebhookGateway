using WebhookGateway.Core.Notifications;

namespace WebhookGateway.Core.Domain;

/// <summary>
/// Correo electrónico suscrito a las alertas de una integración técnica.
/// Cumple con la Ley 1581 y RFC 8058 mediante doble opt-in y token de baja.
/// </summary>
public sealed class IntegrationEmailSubscriber
{
    public int Id { get; set; }

    public int IntegrationId { get; set; }

    public Integration? Integration { get; set; }

    public required string Email { get; set; }

    public SubscriberStatus Status { get; set; } = SubscriberStatus.PendingVerification;

    public string? VerificationToken { get; set; }

    public DateTime? VerificationExpiresAt { get; set; }

    public required string UnsubscribeToken { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? VerifiedAt { get; set; }

    public required string CreatedBy { get; set; }
}
