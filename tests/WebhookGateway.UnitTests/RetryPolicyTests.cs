using Shouldly;
using WebhookGateway.Core.Delivery;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class RetryPolicyTests
{
    private static readonly RetryPolicy Policy = new(
        MaxAttempts: 4,
        Ladder: [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)],
        JitterFactor: 0);

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 60)]
    [InlineData(2, 60)]   // agotada la escalera, se repite el último peldaño
    public void Sube_por_la_escalera_y_se_queda_en_el_ultimo_peldaño(int attemptsMade, int expectedSeconds)
    {
        var delay = Policy.NextDelay(attemptsMade, retryAfter: null, jitterSample: 0.5);

        delay.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Devuelve_null_cuando_se_agotan_los_intentos()
    {
        Policy.NextDelay(attemptsMade: 4, retryAfter: null, jitterSample: 0.5).ShouldBeNull();
        Policy.NextDelay(attemptsMade: 9, retryAfter: null, jitterSample: 0.5).ShouldBeNull();
    }

    [Fact]
    public void Retry_after_del_destino_manda_sobre_la_escalera()
    {
        // Si el destino nos dice cuándo volver, le hacemos caso.
        var delay = Policy.NextDelay(attemptsMade: 0, retryAfter: TimeSpan.FromMinutes(5), jitterSample: 0.5);

        delay.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData(0.0, 8)]    // extremo inferior: 10s - 20%
    [InlineData(0.5, 10)]   // centro: sin desviación
    [InlineData(1.0, 12)]   // extremo superior: 10s + 20%
    public void El_jitter_reparte_alrededor_del_peldaño(double sample, int expectedSeconds)
    {
        var policy = Policy with { JitterFactor = 0.2 };

        var delay = policy.NextDelay(attemptsMade: 0, retryAfter: null, jitterSample: sample);

        delay!.Value.TotalSeconds.ShouldBe(expectedSeconds, tolerance: 0.001);
    }

    [Fact]
    public void El_jitter_nunca_se_sale_del_rango_configurado()
    {
        var policy = Policy with { JitterFactor = 0.2 };

        for (var i = 0; i <= 100; i++)
        {
            var delay = policy.NextDelay(attemptsMade: 0, retryAfter: null, jitterSample: i / 100.0)!.Value;

            delay.ShouldBeInRange(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12));
        }
    }

    [Fact]
    public void La_escalera_por_defecto_es_creciente()
    {
        var ladder = RetryPolicy.Default.Ladder;

        for (var i = 1; i < ladder.Count; i++)
        {
            ladder[i].ShouldBeGreaterThan(ladder[i - 1]);
        }
    }

    [Fact]
    public void No_reintenta_pasada_la_ventana_de_entrega()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var expires = now.AddMinutes(30);

        RetryPolicy.FitsInWindow(now, TimeSpan.FromMinutes(10), expires).ShouldBeTrue();
        RetryPolicy.FitsInWindow(now, TimeSpan.FromMinutes(31), expires).ShouldBeFalse();
    }
}
