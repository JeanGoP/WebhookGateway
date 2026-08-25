using Microsoft.Extensions.Options;
using Shouldly;
using System.Globalization;
using System.Security.Cryptography;
using WebhookGateway.Core.Abstractions;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Security;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class SecretProtectionTests
{
    private static AesGcmSecretProtector Build(int current = 1, params int[] versions)
    {
        var options = new SecretProtectionOptions { CurrentKeyVersion = current };

        foreach (var v in versions.Length > 0 ? versions : [1])
        {
            options.Keys[v.ToString(CultureInfo.InvariantCulture)] = SecretProtectionOptions.GenerateKey();
        }

        return new AesGcmSecretProtector(Options.Create(options));
    }

    [Fact]
    public void Ida_y_vuelta()
    {
        var protector = Build();
        const string secret = "clave-super-secreta-con-ñ-y-emojis-🔐";

        protector.Unprotect(protector.Protect(secret)).ShouldBe(secret);
    }

    [Fact]
    public void Cifrar_dos_veces_lo_mismo_da_bloques_distintos()
    {
        // El nonce es aleatorio por operación. Si esto fallara, GCM estaría roto.
        var protector = Build();

        var a = protector.Protect("igual");
        var b = protector.Protect("igual");

        a.Ciphertext.ShouldNotBe(b.Ciphertext);
    }

    [Fact]
    public void Un_bloque_manipulado_no_descifra()
    {
        var protector = Build();
        var secret = protector.Protect("no me toques");

        secret.Ciphertext[^1] ^= 0xFF;

        Should.Throw<CryptographicException>(() => protector.Unprotect(secret));
    }

    [Fact]
    public void Descifra_con_una_clave_antigua_tras_rotar()
    {
        // Al rotar hay que conservar las claves viejas mientras existan secretos con ellas.
        var options = new SecretProtectionOptions
        {
            CurrentKeyVersion = 1,
            Keys = { ["1"] = SecretProtectionOptions.GenerateKey() },
        };

        var antes = new AesGcmSecretProtector(Options.Create(options)).Protect("dato viejo");

        options.Keys["2"] = SecretProtectionOptions.GenerateKey();
        options.CurrentKeyVersion = 2;
        var despues = new AesGcmSecretProtector(Options.Create(options));

        antes.KeyVersion.ShouldBe(1);
        despues.Protect("dato nuevo").KeyVersion.ShouldBe(2);
        despues.Unprotect(antes).ShouldBe("dato viejo");
    }

    [Fact]
    public void Falta_la_clave_de_esa_version()
    {
        var protector = Build();
        var huerfano = new ProtectedSecret([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12,
                                            13, 14, 15, 16, 17, 18, 19, 20, 21, 22,
                                            23, 24, 25, 26, 27, 28, 29], KeyVersion: 99);

        Should.Throw<CryptographicException>(() => protector.Unprotect(huerfano));
    }

    [Fact]
    public void Un_secreto_vacio_devuelve_cadena_vacia() =>
        Build().Unprotect(ProtectedSecret.Empty).ShouldBe(string.Empty);

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(64)]
    public void Rechaza_claves_que_no_midan_32_bytes(int size)
    {
        var options = new SecretProtectionOptions
        {
            CurrentKeyVersion = 1,
            Keys = { ["1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(size)) },
        };

        Should.Throw<InvalidOperationException>(() => options.Decode());
    }

    [Fact]
    public void Rechaza_una_version_actual_sin_clave()
    {
        var options = new SecretProtectionOptions
        {
            CurrentKeyVersion = 7,
            Keys = { ["1"] = SecretProtectionOptions.GenerateKey() },
        };

        Should.Throw<InvalidOperationException>(() => options.Decode());
    }

    // --- El codec, que es lo que se usa de verdad ---

    [Fact]
    public void El_codec_conserva_la_forma_concreta_de_la_configuracion()
    {
        var codec = new AuthConfigCodec(Build());

        var original = new HmacOutboundAuth(
            Secret: "s3cr3t",
            Algorithm: HmacAlgorithm.HmacSha256,
            SignatureHeader: "X-Signature",
            SigningTemplate: "{timestamp}.{body}",
            TimestampHeader: "X-Timestamp");

        var recuperado = codec.Decode(codec.Encode(original), OutboundAuthType.Hmac);

        recuperado.ShouldBeOfType<HmacOutboundAuth>().ShouldBe(original);
    }

    [Fact]
    public void El_codec_no_descifra_nada_cuando_el_tipo_es_None()
    {
        var codec = new AuthConfigCodec(Build());

        codec.Decode(ProtectedSecret.Empty, OutboundAuthType.None).ShouldBeOfType<NoOutboundAuth>();
        codec.Decode(ProtectedSecret.Empty, InboundAuthType.None).ShouldBeOfType<NoInboundAuth>();
    }
}
