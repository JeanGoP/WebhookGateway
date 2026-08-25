using Shouldly;
using System.Text;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Auth.Validators;
using WebhookGateway.Core.Domain;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class CredentialInboundValidatorsTests
{
    private static InboundRequest Req(Dictionary<string, string>? headers = null, Dictionary<string, string>? query = null) =>
        new(
            headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "{}"u8.ToArray(),
            "POST",
            "/in/x/y",
            "127.0.0.1");

    [Fact]
    public async Task Sin_auth_siempre_pasa() =>
        (await new NoAuthValidator().ValidateAsync(Req(), new NoInboundAuth(), CancellationToken.None))
            .IsSuccess.ShouldBeTrue();

    [Fact]
    public async Task Api_key_correcta_en_cabecera_pasa()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Api-Key"] = "correcta" };
        var config = new ApiKeyInboundAuth("X-Api-Key", "correcta");

        (await new ApiKeyInboundValidator().ValidateAsync(Req(headers), config, CancellationToken.None))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Api_key_incorrecta_falla()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Api-Key"] = "mala" };
        var config = new ApiKeyInboundAuth("X-Api-Key", "correcta");

        var result = await new ApiKeyInboundValidator().ValidateAsync(Req(headers), config, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.invalid_api_key");
    }

    [Fact]
    public async Task Api_key_en_query_string_pasa()
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["token"] = "correcta" };
        var config = new ApiKeyInboundAuth("token", "correcta", ApiKeyLocation.QueryString);

        (await new ApiKeyInboundValidator().ValidateAsync(Req(query: query), config, CancellationToken.None))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Basic_con_credenciales_correctas_pasa()
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = $"Basic {token}" };
        var config = new BasicInboundAuth("user", "pass");

        (await new BasicInboundValidator().ValidateAsync(Req(headers), config, CancellationToken.None))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Basic_sin_cabecera_falla()
    {
        var result = await new BasicInboundValidator().ValidateAsync(Req(), new BasicInboundAuth("u", "p"), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.missing_basic");
    }

    [Fact]
    public async Task Bearer_con_token_correcto_pasa()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer el-token" };

        (await new BearerInboundValidator().ValidateAsync(Req(headers), new BearerInboundAuth("el-token"), CancellationToken.None))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Bearer_con_token_incorrecto_falla()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer otro" };

        var result = await new BearerInboundValidator().ValidateAsync(Req(headers), new BearerInboundAuth("el-token"), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.invalid_bearer");
    }
}
