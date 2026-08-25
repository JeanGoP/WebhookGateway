using Shouldly;
using WebhookGateway.Core.Delivery;
using WebhookGateway.Core.Domain;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class AttemptClassifierTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(299)]
    public void Cualquier_2xx_cierra_la_entrega(int status) =>
        AttemptClassifier.Classify(status).ShouldBe(AttemptVerdict.Success);

    [Fact]
    public void Sin_respuesta_siempre_es_transitorio() =>
        // Timeout o error de red: el destino ni llegó a contestar.
        AttemptClassifier.Classify(null).ShouldBe(AttemptVerdict.Retryable);

    [Theory]
    [InlineData(408)]   // el destino pide que volvamos
    [InlineData(429)]   // nos está limitando
    public void Las_dos_excepciones_de_los_4xx_se_reintentan(int status) =>
        AttemptClassifier.Classify(status).ShouldBe(AttemptVerdict.Retryable);

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public void El_resto_de_4xx_no_se_reintenta(int status) =>
        // El destino rechazó el contenido. Insistir no cambia nada.
        AttemptClassifier.Classify(status).ShouldBe(AttemptVerdict.Permanent);

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void Los_5xx_se_reintentan(int status) =>
        AttemptClassifier.Classify(status).ShouldBe(AttemptVerdict.Retryable);

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    public void Las_redirecciones_son_configuracion_mal_puesta(int status) =>
        // No las seguimos: un destino mal configurado no se arregla insistiendo.
        AttemptClassifier.Classify(status).ShouldBe(AttemptVerdict.Permanent);

    // --- Retry-After ---

    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Retry_after_en_segundos() =>
        AttemptClassifier.ParseRetryAfter("120", Now).ShouldBe(TimeSpan.FromMinutes(2));

    [Fact]
    public void Retry_after_como_fecha_http() =>
        AttemptClassifier.ParseRetryAfter("Fri, 21 Aug 2026 12:05:00 GMT", Now)
            .ShouldBe(TimeSpan.FromMinutes(5));

    [Fact]
    public void Retry_after_en_el_pasado_significa_ya() =>
        AttemptClassifier.ParseRetryAfter("Fri, 21 Aug 2026 11:00:00 GMT", Now)
            .ShouldBe(TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mañana")]
    [InlineData("-5")]
    public void Un_retry_after_ilegible_se_ignora(string? value) =>
        AttemptClassifier.ParseRetryAfter(value, Now).ShouldBeNull();
}
