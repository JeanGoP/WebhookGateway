using Shouldly;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Delivery;
using WebhookGateway.Core.Domain;
using WebhookGateway.Dispatcher;
using WebhookGateway.Dispatcher.Sending;
using Xunit;

namespace WebhookGateway.UnitTests;

/// <summary>
/// El tamaño del claim decide si un lote cabe en su lease. Si no cabe, el lease vence a mitad de
/// lote y esas entregas pueden enviarse dos veces.
/// </summary>
public sealed class ClaimSizingTests
{
    private const int Tope = 20;
    private const int Lease = 180;

    private static OutboundTarget Target(int maxConcurrency = 4, int ratePerMinute = 600, int timeoutSeconds = 30) =>
        new(
            Id: 1,
            IntegrationId: 1,
            IntegrationName: "Pruebas",
            EndpointName: "Destino",
            TargetUrl: new Uri("https://ejemplo.invalid/hook"),
            Method: HttpMethod.Post,
            AuthType: OutboundAuthType.None,
            AuthConfig: new NoOutboundAuth(),
            CustomHeaders: new Dictionary<string, string>(),
            RateLimitPerMinute: ratePerMinute,
            MaxConcurrency: maxConcurrency,
            Timeout: TimeSpan.FromSeconds(timeoutSeconds),
            RetryPolicy: RetryPolicy.Default,
            BreakerFailureThreshold: 5,
            BreakerOpenSeconds: 60);

    [Fact]
    public void Un_destino_rapido_reclama_el_tope()
    {
        ClaimSizing.For(Target(maxConcurrency: 8, ratePerMinute: 0, timeoutSeconds: 10), Tope, Lease).ShouldBe(Tope);
    }

    [Fact]
    public void Con_los_valores_por_defecto_cabe_en_el_lease_aunque_todo_agote_el_timeout()
    {
        // 135 s de presupuesto, 4 a la vez, 30 s de peor caso: 4 rondas de 4.
        var lote = ClaimSizing.For(Target(), Tope, Lease);

        lote.ShouldBe(16);
        (Math.Ceiling(lote / 4.0) * 30).ShouldBeLessThan(Lease);
    }

    [Fact]
    public void Un_destino_lento_de_una_en_una_reclama_pocas()
    {
        ClaimSizing.For(Target(maxConcurrency: 1), Tope, Lease).ShouldBe(4);
    }

    [Fact]
    public void Un_destino_de_ritmo_bajo_reclama_lo_que_su_ritmo_deja_enviar_dentro_del_lease()
    {
        // 6/min es una cada 10 s: veinte serían 200 s y el lease dura 180.
        var lote = ClaimSizing.For(Target(ratePerMinute: 6), Tope, Lease);

        lote.ShouldBe(13);
        (lote * 10).ShouldBeLessThan(Lease);
    }

    [Fact]
    public void Un_timeout_mayor_que_el_lease_reclama_de_una_en_una()
    {
        ClaimSizing.For(Target(timeoutSeconds: 300), Tope, Lease).ShouldBe(1);
    }
}
