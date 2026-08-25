using Shouldly;
using System.Text;
using WebhookGateway.Core.Domain;
using WebhookGateway.Core.Reception;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class DedupeKeyExtractorTests
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Sin_estrategia_no_hay_clave() =>
        DedupeKeyExtractor.Extract(DedupeStrategy.None, null, NoHeaders, "{}"u8).ShouldBeNull();

    [Fact]
    public void De_cabecera_toma_su_valor()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Event-Id"] = "evt-123" };

        DedupeKeyExtractor.Extract(DedupeStrategy.Header, "X-Event-Id", headers, "{}"u8).ShouldBe("evt-123");
    }

    [Fact]
    public void De_cabecera_ausente_no_hay_clave() =>
        DedupeKeyExtractor.Extract(DedupeStrategy.Header, "X-Event-Id", NoHeaders, "{}"u8).ShouldBeNull();

    [Fact]
    public void De_json_path_simple()
    {
        var body = Encoding.UTF8.GetBytes("""{"evento":{"id":"abc-1"}}""");

        DedupeKeyExtractor.Extract(DedupeStrategy.JsonPath, "evento.id", NoHeaders, body).ShouldBe("abc-1");
    }

    [Fact]
    public void De_json_path_inexistente_no_hay_clave()
    {
        var body = Encoding.UTF8.GetBytes("""{"evento":{"id":"abc-1"}}""");

        DedupeKeyExtractor.Extract(DedupeStrategy.JsonPath, "otro.campo", NoHeaders, body).ShouldBeNull();
    }

    [Fact]
    public void De_json_path_con_cuerpo_invalido_no_hay_clave() =>
        DedupeKeyExtractor.Extract(DedupeStrategy.JsonPath, "evento.id", NoHeaders, "no es json"u8).ShouldBeNull();

    [Fact]
    public void Body_hash_es_deterministico_y_de_64_caracteres_hex()
    {
        var a = DedupeKeyExtractor.Extract(DedupeStrategy.BodyHash, null, NoHeaders, "el mismo cuerpo"u8);
        var b = DedupeKeyExtractor.Extract(DedupeStrategy.BodyHash, null, NoHeaders, "el mismo cuerpo"u8);

        a.ShouldBe(b);
        a!.Length.ShouldBe(64);
    }
}
