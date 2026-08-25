using Shouldly;
using WebhookGateway.Core.Reception;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class HeaderMaskingTests
{
    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Api-Key")]
    [InlineData("Cookie")]
    [InlineData("X-Hub-Signature-256")]
    public void Cabeceras_sensibles_se_enmascaran(string header)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [header] = "valor-secreto" };

        HeaderMasking.Mask(headers)[header].ShouldBe("***");
    }

    [Fact]
    public void Cabeceras_normales_se_conservan()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "application/json" };

        HeaderMasking.Mask(headers)["Content-Type"].ShouldBe("application/json");
    }
}
