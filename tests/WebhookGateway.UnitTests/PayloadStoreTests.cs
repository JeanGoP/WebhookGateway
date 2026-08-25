using Shouldly;
using System.Globalization;
using System.Text;
using WebhookGateway.Core.Abstractions;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Payloads;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class PayloadStoreTests
{
    private readonly GzipPayloadStore _store = new();

    [Fact]
    public async Task Los_cuerpos_pequeños_no_se_comprimen()
    {
        // La cabecera de gzip pesaría más que lo que ahorra.
        var body = Encoding.UTF8.GetBytes("""{"evento":"ping"}""");

        var stored = await _store.SaveAsync(1, body, CancellationToken.None);

        stored.Encoding.ShouldBe(PayloadEncoding.Raw);
        stored.SizeBytes.ShouldBe(body.Length);
    }

    [Fact]
    public async Task Un_json_realista_se_comprime_mucho()
    {
        var json = BuildJson(items: 200);
        var body = Encoding.UTF8.GetBytes(json);

        var stored = await _store.SaveAsync(1, body, CancellationToken.None);

        stored.Encoding.ShouldBe(PayloadEncoding.Gzip);
        stored.SizeBytes.ShouldBe(body.Length);            // tamaño original, no el comprimido
        stored.Body!.Length.ShouldBeLessThan(body.Length / 2);
    }

    [Fact]
    public async Task Ida_y_vuelta_de_un_cuerpo_comprimido()
    {
        var json = BuildJson(items: 200);
        var body = Encoding.UTF8.GetBytes(json);

        var stored = await _store.SaveAsync(1, body, CancellationToken.None);
        var loaded = await _store.LoadAsync(stored, CancellationToken.None);

        Encoding.UTF8.GetString(loaded.Span).ShouldBe(json);
    }

    [Fact]
    public async Task Los_datos_incompresibles_se_guardan_crudos()
    {
        // Bytes aleatorios: gzip los haría crecer, así que se dejan tal cual.
        var body = new byte[4096];
        System.Security.Cryptography.RandomNumberGenerator.Fill(body);

        var stored = await _store.SaveAsync(1, body, CancellationToken.None);

        stored.Encoding.ShouldBe(PayloadEncoding.Raw);
        stored.Body.ShouldBe(body);
    }

    [Fact]
    public async Task Un_cuerpo_externo_no_se_puede_leer_desde_el_store_inline()
    {
        var externo = new StoredPayload(PayloadEncoding.Gzip, 100, null, "s3://bucket/objeto");

        await Should.ThrowAsync<NotSupportedException>(
            () => _store.LoadAsync(externo, CancellationToken.None));
    }

    private static string BuildJson(int items)
    {
        var sb = new StringBuilder("""{"pedidos":[""");

        for (var i = 0; i < items; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(CultureInfo.InvariantCulture, $$"""{"id":{{i}},"estado":"creado","cliente":"ACME S.A.","total":1234.56}""");
        }

        return sb.Append("]}").ToString();
    }
}
