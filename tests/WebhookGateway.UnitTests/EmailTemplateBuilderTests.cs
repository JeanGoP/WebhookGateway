using Shouldly;
using WebhookGateway.Core.Notifications;
using WebhookGateway.Dispatcher.Notifications;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class EmailTemplateBuilderTests
{
    [Fact]
    public void BuildIncidentEmail_escapa_caracteres_html_para_evitar_inyecciones()
    {
        var details = new IncidentDetails(
            IntegrationName: "SAP <script>alert('xss')</script>",
            EndpointName: "ERP & Clientes \"quoted\"",
            TargetUrl: "https://api.empresa.com/webhooks?token=<secret>",
            StatusCode: 504,
            ErrorSummary: "Error <critico> & grave",
            AttemptCount: 8,
            OccurredAtUtc: new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc),
            DashboardUrl: "https://gateway.empresa.com/deliveries/100?ref=alert");

        var (html, text) = EmailTemplateBuilder.BuildIncidentEmail(
            "[ALERTA] Test",
            NotificationAlertType.DeadLetter,
            details);

        html.ShouldNotContain("<script>alert('xss')</script>");
        html.ShouldContain("&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;");
        html.ShouldContain("ERP &amp; Clientes &quot;quoted&quot;");
        html.ShouldContain("EVENTO DESCARTADO (DEAD LETTER)");

        text.ShouldContain("SAP <script>alert('xss')</script>");
        text.ShouldContain("HTTP 504");
        text.ShouldContain("8");
    }

    [Theory]
    [InlineData(NotificationAlertType.EndpointDown, "ENDPOINT CAÍDO")]
    [InlineData(NotificationAlertType.EndpointDegraded, "ENDPOINT DEGRADADO")]
    [InlineData(NotificationAlertType.EndpointRecovered, "ENDPOINT RECUPERADO")]
    [InlineData(NotificationAlertType.InboundError, "ERROR DE RECEPCIÓN")]
    [InlineData(NotificationAlertType.DeadLetter, "EVENTO DESCARTADO (DEAD LETTER)")]
    public void BuildIncidentEmail_asigna_etiquetas_segun_tipo_de_alerta(NotificationAlertType type, string badgeEsperado)
    {
        var details = new IncidentDetails(
            IntegrationName: "Shopify",
            EndpointName: "Ordenes",
            TargetUrl: "https://shopify.com/in",
            StatusCode: 500,
            ErrorSummary: "Fallo",
            AttemptCount: 1,
            OccurredAtUtc: DateTime.UtcNow,
            DashboardUrl: "https://panel.local");

        var (html, text) = EmailTemplateBuilder.BuildIncidentEmail("Alerta", type, details);

        html.ShouldContain(badgeEsperado);
        text.ShouldContain(badgeEsperado);
    }
}
