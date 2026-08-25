using WebhookGateway.Core.Abstractions;

namespace WebhookGateway.Core.Domain;

/// <summary>
/// Una URL pública de recepción: <c>POST /in/{integración}/{slug}</c>.
/// </summary>
public sealed class InboundEndpoint
{
    public int Id { get; set; }

    public int IntegrationId { get; set; }

    public Integration? Integration { get; set; }

    public required string Name { get; set; }

    /// <summary>Segundo segmento de la URL pública. Único dentro de su integración.</summary>
    public required string Slug { get; set; }

    public bool IsActive { get; set; } = true;

    public InboundAuthType AuthType { get; set; } = InboundAuthType.None;

    /// <summary>JSON de configuración cifrado con AES-GCM. Nunca sale por la API.</summary>
    public byte[] AuthConfigCipher { get; set; } = [];

    public int AuthConfigKeyVersion { get; set; }

    public DedupeStrategy DedupeStrategy { get; set; } = DedupeStrategy.None;

    /// <summary>
    /// Nombre de cabecera o ruta JSONPath, según la estrategia. Nulo para
    /// <see cref="DedupeStrategy.None"/> y <see cref="DedupeStrategy.BodyHash"/>.
    /// </summary>
    public string? DedupeSource { get; set; }

    /// <summary>Se rechaza con 413 por encima de esto. Protege memoria y almacenamiento.</summary>
    public int MaxBodyBytes { get; set; } = 1024 * 1024;

    public DateTime CreatedAt { get; set; }

    public ICollection<Subscription> Subscriptions { get; } = [];

    /// <summary>Vista del par cifrado. No se mapea: son las dos columnas de arriba.</summary>
    public ProtectedSecret AuthConfig
    {
        get => new(AuthConfigCipher, AuthConfigKeyVersion);
        set
        {
            AuthConfigCipher = value.Ciphertext;
            AuthConfigKeyVersion = value.KeyVersion;
        }
    }
}
