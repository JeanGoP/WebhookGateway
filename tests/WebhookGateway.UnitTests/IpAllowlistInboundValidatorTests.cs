using Shouldly;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Auth.Validators;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class IpAllowlistInboundValidatorTests
{
    private static InboundRequest Req(string sourceIp) =>
        new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "{}"u8.ToArray(), "POST", "/in/x/y", sourceIp);

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.0/24")]
    [InlineData("203.0.113.5", "203.0.113.5/32")]
    [InlineData("::1", "::1/128")]
    public async Task Ip_dentro_del_rango_pasa(string ip, string cidr)
    {
        var result = await new IpAllowlistInboundValidator()
            .ValidateAsync(Req(ip), new IpAllowlistInboundAuth([cidr]), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Ip_fuera_del_rango_falla()
    {
        var result = await new IpAllowlistInboundValidator()
            .ValidateAsync(Req("198.51.100.9"), new IpAllowlistInboundAuth(["203.0.113.0/24"]), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.ip_not_allowed");
    }

    [Fact]
    public async Task Cualquiera_de_varios_rangos_basta()
    {
        var config = new IpAllowlistInboundAuth(["10.0.0.0/8", "203.0.113.0/24"]);

        var result = await new IpAllowlistInboundValidator().ValidateAsync(Req("203.0.113.9"), config, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Ip_de_origen_ilegible_falla()
    {
        var result = await new IpAllowlistInboundValidator()
            .ValidateAsync(Req("no-es-una-ip"), new IpAllowlistInboundAuth(["10.0.0.0/8"]), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.unresolvable_ip");
    }
}
