using Shouldly;
using System.Text;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Auth.Providers;
using WebhookGateway.Core.Auth.Validators;
using WebhookGateway.Core.Domain;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class OutboundAuthProviderTests
{
    private static HttpRequestMessage Request(string url = "https://destino.example/hook") =>
        new(HttpMethod.Post, url);

    [Fact]
    public async Task Api_key_va_en_la_cabecera_configurada()
    {
        using var request = Request();

        await new ApiKeyOutboundProvider().ApplyAsync(
            request, new ApiKeyOutboundAuth("X-Api-Key", "secreto"), default, 1, CancellationToken.None);

        request.Headers.GetValues("X-Api-Key").ShouldHaveSingleItem().ShouldBe("secreto");
    }

    [Fact]
    public async Task Api_key_en_query_se_escapa()
    {
        using var request = Request();

        await new ApiKeyOutboundProvider().ApplyAsync(
            request, new ApiKeyOutboundAuth("api_key", "a b&c", ApiKeyLocation.QueryString), default, 1, CancellationToken.None);

        request.RequestUri!.AbsoluteUri.ShouldBe("https://destino.example/hook?api_key=a%20b%26c");
    }

    [Fact]
    public async Task Api_key_en_query_conserva_lo_que_ya_habia()
    {
        using var request = Request("https://destino.example/hook?ya=1");

        await new ApiKeyOutboundProvider().ApplyAsync(
            request, new ApiKeyOutboundAuth("api_key", "k", ApiKeyLocation.QueryString), default, 1, CancellationToken.None);

        request.RequestUri!.Query.ShouldBe("?ya=1&api_key=k");
    }

    [Fact]
    public async Task Api_key_admite_prefijo()
    {
        // Hay destinos que piden la clave como "Authorization: Token abc".
        using var request = Request();

        await new ApiKeyOutboundProvider().ApplyAsync(
            request, new ApiKeyOutboundAuth("Authorization", "abc", ApiKeyLocation.Header, "Token "), default, 1, CancellationToken.None);

        request.Headers.GetValues("Authorization").ShouldHaveSingleItem().ShouldBe("Token abc");
    }

    [Fact]
    public async Task Basic_codifica_usuario_y_contraseña()
    {
        using var request = Request();

        await new BasicOutboundProvider().ApplyAsync(
            request, new BasicOutboundAuth("user", "pass"), default, 1, CancellationToken.None);

        request.Headers.Authorization!.Scheme.ShouldBe("Basic");
        request.Headers.Authorization.Parameter.ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass")));
    }

    [Fact]
    public async Task Bearer_pone_el_token()
    {
        using var request = Request();

        await new BearerOutboundProvider().ApplyAsync(
            request, new BearerOutboundAuth("tok"), default, 1, CancellationToken.None);

        request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        request.Headers.Authorization.Parameter.ShouldBe("tok");
    }

    /// <summary>
    /// El test que de verdad importa: lo que firmamos al salir tiene que poder validarlo
    /// nuestro propio validador de entrada. Si estos dos se desincronizan, ninguna prueba
    /// de un lado solo lo detectaría.
    /// </summary>
    [Fact]
    public async Task Hmac_ida_y_vuelta_entre_salida_y_entrada()
    {
        var body = Encoding.UTF8.GetBytes("""{"pedido":42}""");
        using var request = Request("https://destino.example/hooks/pedidos");

        await new HmacOutboundProvider(TimeProvider.System).ApplyAsync(
            request,
            new HmacOutboundAuth("compartido", HmacAlgorithm.HmacSha256, "X-Signature", "{timestamp}.{body}", "X-Timestamp", "sha256="),
            body, 1, CancellationToken.None);

        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.First(), StringComparer.OrdinalIgnoreCase);

        var inbound = new InboundRequest(
            headers, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            body, "POST", "/hooks/pedidos", "127.0.0.1");

        var config = new HmacInboundAuth("compartido", HmacAlgorithm.HmacSha256, "X-Signature", "{timestamp}.{body}", "X-Timestamp", 300, "sha256=");

        var result = await new HmacInboundValidator(TimeProvider.System).ValidateAsync(inbound, config, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error.ToString());
    }

    [Fact]
    public async Task Hmac_detecta_un_cuerpo_alterado_en_transito()
    {
        var original = Encoding.UTF8.GetBytes("""{"pedido":42}""");
        using var request = Request("https://destino.example/hooks/pedidos");

        await new HmacOutboundProvider(TimeProvider.System).ApplyAsync(
            request,
            new HmacOutboundAuth("compartido", HmacAlgorithm.HmacSha256, "X-Signature", "{timestamp}.{body}", "X-Timestamp"),
            original, 1, CancellationToken.None);

        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.First(), StringComparer.OrdinalIgnoreCase);

        var tampered = new InboundRequest(
            headers, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Encoding.UTF8.GetBytes("""{"pedido":43}"""), "POST", "/hooks/pedidos", "127.0.0.1");

        var config = new HmacInboundAuth("compartido", HmacAlgorithm.HmacSha256, "X-Signature", "{timestamp}.{body}", "X-Timestamp");

        var result = await new HmacInboundValidator(TimeProvider.System).ValidateAsync(tampered, config, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.invalid_signature");
    }

    [Fact]
    public async Task Hmac_sin_timestamp_no_añade_cabecera_vacia()
    {
        using var request = Request();

        await new HmacOutboundProvider(TimeProvider.System).ApplyAsync(
            request, new HmacOutboundAuth("s", HmacAlgorithm.HmacSha256, "X-Sig", "{body}"), "hola"u8.ToArray(), 1, CancellationToken.None);

        request.Headers.Contains("X-Sig").ShouldBeTrue();
        request.Headers.Contains("X-Timestamp").ShouldBeFalse();
    }
}
