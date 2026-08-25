using System.Text.Json;
using WebhookGateway.Core.Abstractions;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Data.Security;

/// <summary>
/// Convierte una configuración de autenticación en un bloque cifrado y viceversa.
/// </summary>
/// <remarks>
/// Se cifra el JSON <b>entero</b>, no campo por campo. Nada de ahí dentro se consulta
/// nunca, así que no hay razón para dejar partes en claro, y así es imposible olvidarse
/// de cifrar un campo nuevo el día que se añada uno.
/// <para>
/// El tipo concreto al que deserializar sale del <c>AuthType</c> del endpoint, no de un
/// discriminador dentro del JSON: menos que escribir y un formato que no puede
/// desincronizarse de la columna.
/// </para>
/// </remarks>
public sealed class AuthConfigCodec(ISecretProtector protector)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ProtectedSecret Encode(OutboundAuthConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return protector.Protect(JsonSerializer.Serialize(config, config.GetType(), Json));
    }

    public ProtectedSecret Encode(InboundAuthConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return protector.Protect(JsonSerializer.Serialize(config, config.GetType(), Json));
    }

    /// <summary>
    /// Recibe el <c>authConfig</c> del request como <see cref="JsonElement"/> crudo,
    /// lo deserializa al tipo concreto que indica <paramref name="type"/> y lo cifra.
    /// </summary>
    public ProtectedSecret Encode(JsonElement authConfig, InboundAuthType type)
    {
        var config = (InboundAuthConfig?)JsonSerializer.Deserialize(
            authConfig.GetRawText(), InboundAuthConfig.TypeFor(type), Json)
            ?? throw new JsonException(
                $"La configuración de autenticación de entrada ({type}) es inválida.");
        return Encode(config);
    }

    /// <inheritdoc cref="Encode(JsonElement, InboundAuthType)"/>
    public ProtectedSecret Encode(JsonElement authConfig, OutboundAuthType type)
    {
        var config = (OutboundAuthConfig?)JsonSerializer.Deserialize(
            authConfig.GetRawText(), OutboundAuthConfig.TypeFor(type), Json)
            ?? throw new JsonException(
                $"La configuración de autenticación de salida ({type}) es inválida.");
        return Encode(config);
    }

    public OutboundAuthConfig Decode(ProtectedSecret secret, OutboundAuthType type)
    {
        if (type == OutboundAuthType.None || secret.IsEmpty)
        {
            return new NoOutboundAuth();
        }

        var json = protector.Unprotect(secret);
        return (OutboundAuthConfig)Deserialize(json, OutboundAuthConfig.TypeFor(type), type);
    }

    public InboundAuthConfig Decode(ProtectedSecret secret, InboundAuthType type)
    {
        if (type == InboundAuthType.None || secret.IsEmpty)
        {
            return new NoInboundAuth();
        }

        var json = protector.Unprotect(secret);
        return (InboundAuthConfig)Deserialize(json, InboundAuthConfig.TypeFor(type), type);
    }

    private static object Deserialize(string json, Type target, object type) =>
        JsonSerializer.Deserialize(json, target, Json)
        ?? throw new InvalidOperationException(
            $"La configuración de autenticación de tipo {type} está vacía o corrupta. " +
            "Vuelve a guardarla desde el panel.");
}
