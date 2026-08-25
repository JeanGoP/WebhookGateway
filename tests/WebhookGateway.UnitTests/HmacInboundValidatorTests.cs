using Shouldly;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Auth.Validators;
using WebhookGateway.Core.Domain;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class HmacInboundValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static InboundRequest Req(string body, string signature, string? timestamp = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Signature"] = signature };
        if (timestamp is not null)
        {
            headers["X-Timestamp"] = timestamp;
        }

        return new InboundRequest(
            headers, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Encoding.UTF8.GetBytes(body), "POST", "/in/x/y", "127.0.0.1");
    }

    private static string Sign(string secret, string data) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(data)));

    [Fact]
    public async Task Firma_correcta_sin_timestamp_pasa()
    {
        const string body = """{"evento":"x"}""";
        var config = new HmacInboundAuth("secreto", HmacAlgorithm.HmacSha256, "X-Signature", "{body}");

        var result = await new HmacInboundValidator(new FixedTimeProvider(Now))
            .ValidateAsync(Req(body, Sign("secreto", body)), config, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Firma_incorrecta_falla()
    {
        const string body = """{"evento":"x"}""";
        var config = new HmacInboundAuth("secreto", HmacAlgorithm.HmacSha256, "X-Signature", "{body}");

        var result = await new HmacInboundValidator(new FixedTimeProvider(Now))
            .ValidateAsync(Req(body, "no-es-la-firma"), config, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.invalid_signature");
    }

    [Fact]
    public async Task Prefijo_de_firma_se_respeta()
    {
        const string body = "hola";
        var config = new HmacInboundAuth(
            "secreto", HmacAlgorithm.HmacSha256, "X-Signature", "{body}", SignaturePrefix: "sha256=");

        var result = await new HmacInboundValidator(new FixedTimeProvider(Now))
            .ValidateAsync(Req(body, "sha256=" + Sign("secreto", body)), config, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Timestamp_dentro_de_la_tolerancia_pasa()
    {
        const string body = "hola";
        var timestamp = Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var config = new HmacInboundAuth(
            "secreto", HmacAlgorithm.HmacSha256, "X-Signature", "{timestamp}.{body}", "X-Timestamp", ToleranceSeconds: 300);

        var result = await new HmacInboundValidator(new FixedTimeProvider(Now))
            .ValidateAsync(Req(body, Sign("secreto", $"{timestamp}.{body}"), timestamp), config, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Timestamp_fuera_de_la_tolerancia_falla()
    {
        const string body = "hola";
        var oldTimestamp = Now.AddMinutes(-10).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var config = new HmacInboundAuth(
            "secreto", HmacAlgorithm.HmacSha256, "X-Signature", "{timestamp}.{body}", "X-Timestamp", ToleranceSeconds: 300);

        var result = await new HmacInboundValidator(new FixedTimeProvider(Now))
            .ValidateAsync(Req(body, Sign("secreto", $"{oldTimestamp}.{body}"), oldTimestamp), config, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.expired_signature");
    }

    [Fact]
    public async Task Falta_la_cabecera_de_firma()
    {
        var config = new HmacInboundAuth("secreto", HmacAlgorithm.HmacSha256, "X-Signature", "{body}");
        var request = new InboundRequest(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "hola"u8.ToArray(), "POST", "/in/x/y", "127.0.0.1");

        var result = await new HmacInboundValidator(new FixedTimeProvider(Now)).ValidateAsync(request, config, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.missing_signature");
    }
}
