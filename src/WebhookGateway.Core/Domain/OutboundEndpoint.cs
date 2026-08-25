using WebhookGateway.Core.Abstractions;
using WebhookGateway.Core.Delivery;

namespace WebhookGateway.Core.Domain;

/// <summary>
/// Un destino al que entregamos. Aquí vive todo lo que hace que el gateway no sature al
/// sistema del otro lado: velocidad, concurrencia, reintentos y umbral del circuito.
/// </summary>
public sealed class OutboundEndpoint
{
    public int Id { get; set; }

    public int IntegrationId { get; set; }

    public Integration? Integration { get; set; }

    public required string Name { get; set; }

    public required string TargetUrl { get; set; }

    public string HttpMethod { get; set; } = "POST";

    public bool IsActive { get; set; } = true;

    public OutboundAuthType AuthType { get; set; } = OutboundAuthType.None;

    /// <summary>JSON de configuración cifrado con AES-GCM. Nunca sale por la API.</summary>
    public byte[] AuthConfigCipher { get; set; } = [];

    public int AuthConfigKeyVersion { get; set; }

    /// <summary>
    /// Cabeceras fijas añadidas a cada entrega, como JSON. Son ortogonales a la
    /// autenticación: un destino puede pedir Bearer y además un <c>X-Tenant-Id</c>.
    /// </summary>
    public string? CustomHeadersJson { get; set; }

    // --- Control de velocidad: el corazón del producto ---

    /// <summary>Presupuesto del token bucket. Cero o menos significa sin límite.</summary>
    public int RateLimitPerMinute { get; set; } = 600;

    /// <summary>Peticiones simultáneas. Se aplica vía MaxConnectionsPerServer.</summary>
    public int MaxConcurrency { get; set; } = 4;

    public int TimeoutSeconds { get; set; } = 30;

    // --- Reintentos ---

    public int MaxAttempts { get; set; } = 8;

    /// <summary>
    /// Ventana de entrega. Pasada, la entrega se marca <see cref="DeliveryStatus.Expired"/>
    /// y deja de reintentarse. Sin esto, un destino muerto acumula reintentos zombi.
    /// </summary>
    public int DeliveryWindowHours { get; set; } = 72;

    /// <summary>Escalera de esperas en segundos, como JSON. Nulo usa la escalera por defecto.</summary>
    public string? BackoffLadderJson { get; set; }

    // --- Circuit breaker ---

    public int BreakerFailureThreshold { get; set; } = 5;

    public int BreakerOpenSeconds { get; set; } = 60;

    public DateTime CreatedAt { get; set; }

    public ICollection<Subscription> Subscriptions { get; } = [];

    public ProtectedSecret AuthConfig
    {
        get => new(AuthConfigCipher, AuthConfigKeyVersion);
        set
        {
            AuthConfigCipher = value.Ciphertext;
            AuthConfigKeyVersion = value.KeyVersion;
        }
    }

    public RetryPolicy ResolveRetryPolicy(IReadOnlyList<TimeSpan>? ladder = null) =>
        RetryPolicy.Default with
        {
            MaxAttempts = MaxAttempts,
            Ladder = ladder ?? RetryPolicy.Default.Ladder,
        };
}
