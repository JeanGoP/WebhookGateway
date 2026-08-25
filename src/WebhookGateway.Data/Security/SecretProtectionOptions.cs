using System.Security.Cryptography;

namespace WebhookGateway.Data.Security;

/// <summary>
/// Claves maestras de cifrado, indexadas por versión.
/// </summary>
/// <remarks>
/// Las claves vienen de variables de entorno, nunca del repositorio ni de
/// <c>appsettings.json</c>. En Render se configuran como variables del servicio:
/// <code>
/// Gateway__Secrets__CurrentKeyVersion=1
/// Gateway__Secrets__Keys__1=&lt;32 bytes en base64&gt;
/// </code>
/// Para rotar: se añade la versión 2, se sube <c>CurrentKeyVersion</c> a 2 y se deja la 1
/// para poder seguir descifrando lo antiguo. Cuando ya nada la use, se retira.
/// </remarks>
public sealed class SecretProtectionOptions
{
    public const string SectionName = "Gateway:Secrets";

    /// <summary>Versión con la que se cifra todo lo nuevo.</summary>
    public int CurrentKeyVersion { get; set; }

    /// <summary>Claves en base64, de 32 bytes cada una, por versión.</summary>
    public Dictionary<string, string> Keys { get; init; } = [];

    /// <summary>
    /// Valida y decodifica. Se llama al arrancar: es preferible que la aplicación no
    /// levante a que levante y falle al cifrar el primer secreto.
    /// </summary>
    public IReadOnlyDictionary<int, byte[]> Decode()
    {
        if (Keys.Count == 0)
        {
            throw new InvalidOperationException(
                $"No hay claves de cifrado configuradas. Define {SectionName}:Keys:<versión> con 32 bytes en base64.");
        }

        var decoded = new Dictionary<int, byte[]>(Keys.Count);

        foreach (var (versionText, base64) in Keys)
        {
            if (!int.TryParse(versionText, out var version))
            {
                throw new InvalidOperationException(
                    $"La versión de clave '{versionText}' no es un número. Usa {SectionName}:Keys:1, :2, etc.");
            }

            var key = Convert.FromBase64String(base64);

            if (key.Length != 32)
            {
                throw new InvalidOperationException(
                    $"La clave de versión {version} mide {key.Length} bytes; se requieren 32 (AES-256).");
            }

            decoded[version] = key;
        }

        if (!decoded.ContainsKey(CurrentKeyVersion))
        {
            throw new InvalidOperationException(
                $"CurrentKeyVersion es {CurrentKeyVersion} pero no hay ninguna clave con esa versión.");
        }

        return decoded;
    }

    /// <summary>Genera una clave nueva en base64. Para usar al configurar el entorno.</summary>
    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
