using WebhookGateway.Core.Domain;

namespace WebhookGateway.Core.Auth;

/// <summary>
/// Configuración de autenticación de un destino. Se guarda como JSON cifrado; el
/// <see cref="OutboundAuthType"/> del endpoint dice a qué tipo concreto deserializarlo,
/// así que no hacen falta discriminadores dentro del JSON.
/// </summary>
public abstract record OutboundAuthConfig
{
    public static Type TypeFor(OutboundAuthType type) => type switch
    {
        OutboundAuthType.None => typeof(NoOutboundAuth),
        OutboundAuthType.ApiKey => typeof(ApiKeyOutboundAuth),
        OutboundAuthType.Basic => typeof(BasicOutboundAuth),
        OutboundAuthType.Bearer => typeof(BearerOutboundAuth),
        OutboundAuthType.Hmac => typeof(HmacOutboundAuth),
        OutboundAuthType.OAuth2ClientCredentials => typeof(OAuth2OutboundAuth),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Tipo de autenticación de salida desconocido."),
    };
}

public sealed record NoOutboundAuth : OutboundAuthConfig;

/// <summary>
/// El nombre del parámetro es configuración, nunca código: en la práctica aparece como
/// <c>X-API-Key</c>, <c>Api-Key</c>, <c>Authorization</c> o <c>?api_key=</c>.
/// </summary>
public sealed record ApiKeyOutboundAuth(
    string ParameterName,
    string Value,
    ApiKeyLocation Location = ApiKeyLocation.Header,
    string? ValuePrefix = null) : OutboundAuthConfig;

public sealed record BasicOutboundAuth(string Username, string Password) : OutboundAuthConfig;

public sealed record BearerOutboundAuth(string Token) : OutboundAuthConfig;

/// <summary>
/// Firma el cuerpo saliente.
/// </summary>
/// <param name="SigningTemplate">
/// Plantilla del texto a firmar. Marcadores admitidos: <c>{timestamp}</c>, <c>{body}</c>,
/// <c>{method}</c>, <c>{path}</c>. El más común es <c>"{timestamp}.{body}"</c>.
/// </param>
public sealed record HmacOutboundAuth(
    string Secret,
    HmacAlgorithm Algorithm,
    string SignatureHeader,
    string SigningTemplate,
    string? TimestampHeader = null,
    string? SignaturePrefix = null) : OutboundAuthConfig;

/// <summary>
/// La única estrategia con estado. El proveedor cachea el token por endpoint, lo refresca
/// de forma anticipada y protege contra estampidas cuando varias entregas concurrentes
/// encuentran el token vencido a la vez.
/// </summary>
public sealed record OAuth2OutboundAuth(
    string TokenUrl,
    string ClientId,
    string ClientSecret,
    string? Scope = null,
    OAuth2CredentialPlacement Placement = OAuth2CredentialPlacement.BasicHeader,
    IReadOnlyDictionary<string, string>? ExtraParameters = null) : OutboundAuthConfig;
