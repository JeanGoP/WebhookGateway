using WebhookGateway.Core.Domain;

namespace WebhookGateway.Core.Auth;

/// <summary>Configuración de autenticación exigida a quien nos envía webhooks.</summary>
public abstract record InboundAuthConfig
{
    public static Type TypeFor(InboundAuthType type) => type switch
    {
        InboundAuthType.None => typeof(NoInboundAuth),
        InboundAuthType.ApiKey => typeof(ApiKeyInboundAuth),
        InboundAuthType.Basic => typeof(BasicInboundAuth),
        InboundAuthType.Bearer => typeof(BearerInboundAuth),
        InboundAuthType.Hmac => typeof(HmacInboundAuth),
        InboundAuthType.IpAllowlist => typeof(IpAllowlistInboundAuth),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Tipo de autenticación de entrada desconocido."),
    };
}

public sealed record NoInboundAuth : InboundAuthConfig;

public sealed record ApiKeyInboundAuth(
    string ParameterName,
    string ExpectedValue,
    ApiKeyLocation Location = ApiKeyLocation.Header) : InboundAuthConfig;

public sealed record BasicInboundAuth(string Username, string Password) : InboundAuthConfig;

public sealed record BearerInboundAuth(string ExpectedToken) : InboundAuthConfig;

/// <summary>
/// Validación de firma HMAC sobre el cuerpo crudo.
/// </summary>
/// <param name="ToleranceSeconds">
/// Ventana admitida entre el timestamp firmado y ahora. No es opcional: sin ella, una
/// firma válida capturada puede reproducirse indefinidamente. Cero la desactiva, y solo
/// debería usarse con emisores que no envían timestamp.
/// </param>
public sealed record HmacInboundAuth(
    string Secret,
    HmacAlgorithm Algorithm,
    string SignatureHeader,
    string SigningTemplate,
    string? TimestampHeader = null,
    int ToleranceSeconds = 300,
    string? SignaturePrefix = null) : InboundAuthConfig;

/// <param name="AllowedCidrs">Rangos en notación CIDR. Un host suelto se escribe <c>/32</c>.</param>
public sealed record IpAllowlistInboundAuth(IReadOnlyList<string> AllowedCidrs) : InboundAuthConfig;
