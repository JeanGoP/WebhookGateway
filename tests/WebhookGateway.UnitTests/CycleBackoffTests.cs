using Shouldly;
using WebhookGateway.Dispatcher;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class CycleBackoffTests
{
    private const double NoJitter = 0.5;

    [Fact]
    public void Sube_por_la_escalera_con_cada_fallo_seguido()
    {
        var backoff = new CycleBackoff();

        backoff.NextDelay(NoJitter).ShouldBe(TimeSpan.FromSeconds(1));
        backoff.NextDelay(NoJitter).ShouldBe(TimeSpan.FromSeconds(5));
        backoff.NextDelay(NoJitter).ShouldBe(TimeSpan.FromSeconds(15));
        backoff.NextDelay(NoJitter).ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Se_queda_en_el_ultimo_peldaño_y_no_crece_sin_limite()
    {
        var backoff = new CycleBackoff();

        for (int i = 0; i < 20; i++)
        {
            backoff.NextDelay(NoJitter).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
        }

        backoff.NextDelay(NoJitter).ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Un_ciclo_bueno_devuelve_la_escalera_al_primer_peldaño()
    {
        var backoff = new CycleBackoff();

        backoff.NextDelay(NoJitter);
        backoff.NextDelay(NoJitter);
        backoff.ConsecutiveFailures.ShouldBe(2);

        backoff.Reset();

        backoff.ConsecutiveFailures.ShouldBe(0);
        backoff.NextDelay(NoJitter).ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(0.0, 0.8)]   // extremo inferior del rango de jitter
    [InlineData(0.5, 1.0)]
    [InlineData(0.999, 1.2)] // extremo superior
    public void Reparte_la_espera_alrededor_del_peldaño(double jitterSample, double expectedFactor)
    {
        var delay = new CycleBackoff().NextDelay(jitterSample);

        delay.TotalSeconds.ShouldBe(expectedFactor, tolerance: 0.01);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.0)]
    [InlineData(2.5)]
    public void Rechaza_una_muestra_de_jitter_fuera_de_rango(double jitterSample)
    {
        var backoff = new CycleBackoff();

        Should.Throw<ArgumentOutOfRangeException>(() => backoff.NextDelay(jitterSample));
    }
}
