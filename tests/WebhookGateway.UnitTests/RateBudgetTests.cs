using Shouldly;
using WebhookGateway.Dispatcher.Throttling;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class RateBudgetTests
{
    /// <summary>
    /// El caso que motivó el arreglo: con <c>ceil(rate / 60)</c> un destino a 90/min recibía
    /// 120/min, un 33 % más de lo que había pedido.
    /// </summary>
    [Fact]
    public void Noventa_por_minuto_son_noventa_y_no_ciento_veinte()
    {
        var budget = RateBudget.For(90);

        budget.EffectivePerMinute.ShouldBe(90);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(300)]
    [InlineData(600)]
    [InlineData(1200)]
    [InlineData(4200)]
    public void Los_limites_habituales_salen_exactos(int ratePerMinute)
    {
        RateBudget.For(ratePerMinute).EffectivePerMinute.ShouldBe(ratePerMinute, tolerance: 0.001);
    }

    /// <summary>
    /// La regla que de verdad importa: pasarse del límite de un destino es un fallo, quedarse
    /// corto solo es ir más despacio. Así que el redondeo nunca va hacia arriba.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(61)]
    [InlineData(97)]
    [InlineData(599)]
    [InlineData(4209)]
    public void Nunca_concede_mas_de_lo_configurado(int ratePerMinute)
    {
        RateBudget.For(ratePerMinute).EffectivePerMinute.ShouldBeLessThanOrEqualTo(ratePerMinute);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(61)]
    [InlineData(4209)]
    public void Siempre_repone_al_menos_una_ficha(int ratePerMinute)
    {
        RateBudget.For(ratePerMinute).TokensPerPeriod.ShouldBeGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Un periodo largo cumple el límite igual pero suelta todas las fichas de golpe, que es lo
    /// contrario del suavizado. Solo se tolera en ritmos tan bajos que no hay otra salida.
    /// </summary>
    [Theory]
    [InlineData(61)]
    [InlineData(97)]
    [InlineData(599)]
    public void No_alarga_el_periodo_mas_de_diez_segundos(int ratePerMinute)
    {
        RateBudget.For(ratePerMinute).Period.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 30)]
    [InlineData(5, 12)]
    public void Con_ritmos_muy_bajos_usa_el_periodo_exacto(int ratePerMinute, int expectedPeriodSeconds)
    {
        var budget = RateBudget.For(ratePerMinute);

        budget.Period.ShouldBe(TimeSpan.FromSeconds(expectedPeriodSeconds));
        budget.TokensPerPeriod.ShouldBe(1);
        budget.EffectivePerMinute.ShouldBe(ratePerMinute, tolerance: 0.001);
    }

    [Fact]
    public void La_rafaga_nunca_es_menor_que_lo_que_se_repone_de_una_vez()
    {
        foreach (var rate in new[] { 1, 5, 60, 90, 600, 4209 })
        {
            var budget = RateBudget.For(rate);

            budget.BurstLimit.ShouldBeGreaterThanOrEqualTo(budget.TokensPerPeriod);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rechaza_un_limite_que_no_es_un_ritmo(int ratePerMinute) =>
        Should.Throw<ArgumentOutOfRangeException>(() => RateBudget.For(ratePerMinute));
}
