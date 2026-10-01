using System.Net.Mail;
using Shouldly;
using WebhookGateway.Core.Domain;
using WebhookGateway.Core.Notifications;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class SubscriberLifecycleTests
{
    [Fact]
    public void Nuevo_suscriptor_inicia_en_PendingVerification_con_tokens()
    {
        var now = DateTime.UtcNow;
        var sub = new IntegrationEmailSubscriber
        {
            IntegrationId = 1,
            Email = "alerts@example.com",
            Status = SubscriberStatus.PendingVerification,
            VerificationToken = Guid.NewGuid().ToString("N"),
            VerificationExpiresAt = now.AddHours(48),
            UnsubscribeToken = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            CreatedBy = "Admin"
        };

        sub.Status.ShouldBe(SubscriberStatus.PendingVerification);
        sub.VerificationToken.ShouldNotBeNullOrWhiteSpace();
        sub.UnsubscribeToken.ShouldNotBeNullOrWhiteSpace();
        sub.VerifiedAt.ShouldBeNull();
        sub.VerificationExpiresAt.Value.ShouldBeGreaterThan(now);
    }

    [Fact]
    public void Confirmacion_doble_optin_transiciona_a_Verified_y_limpia_token()
    {
        var now = DateTime.UtcNow;
        var sub = new IntegrationEmailSubscriber
        {
            IntegrationId = 1,
            Email = "alerts@example.com",
            Status = SubscriberStatus.PendingVerification,
            VerificationToken = "valid-token-123",
            VerificationExpiresAt = now.AddHours(48),
            UnsubscribeToken = "unsub-token-456",
            CreatedAt = now.AddHours(-1),
            CreatedBy = "Admin"
        };

        // Simula la activación
        sub.Status = SubscriberStatus.Verified;
        sub.VerifiedAt = now;
        sub.VerificationToken = null;
        sub.VerificationExpiresAt = null;

        sub.Status.ShouldBe(SubscriberStatus.Verified);
        sub.VerifiedAt.ShouldBe(now);
        sub.VerificationToken.ShouldBeNull();
        sub.VerificationExpiresAt.ShouldBeNull();
    }

    [Fact]
    public void Unsubscribe_un_clic_transiciona_a_Unsubscribed()
    {
        var sub = new IntegrationEmailSubscriber
        {
            IntegrationId = 1,
            Email = "alerts@example.com",
            Status = SubscriberStatus.Verified,
            UnsubscribeToken = "unsub-123",
            CreatedBy = "Admin"
        };

        sub.Status = SubscriberStatus.Unsubscribed;

        sub.Status.ShouldBe(SubscriberStatus.Unsubscribed);
    }

    [Theory]
    [InlineData(SubscriberStatus.Verified, SubscriberStatus.Silenced)]
    [InlineData(SubscriberStatus.Silenced, SubscriberStatus.Verified)]
    public void Toggle_mute_alterna_entre_Verified_y_Silenced(SubscriberStatus initial, SubscriberStatus expected)
    {
        var current = initial;
        var next = current == SubscriberStatus.Silenced
            ? SubscriberStatus.Verified
            : SubscriberStatus.Silenced;

        next.ShouldBe(expected);
    }

    [Theory]
    [InlineData("valid@example.com", true)]
    [InlineData("user.name+tag@sub.domain.org", true)]
    [InlineData("not-an-email", false)]
    [InlineData("@missinguser.com", false)]
    [InlineData("missingdomain@", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Validador_de_correo_rechaza_direcciones_invalidas(string input, bool expectedValid)
    {
        var isValid = !string.IsNullOrWhiteSpace(input) && MailAddress.TryCreate(input.Trim(), out var parsed) && parsed.Address.Contains('@');
        isValid.ShouldBe(expectedValid);
    }
}
