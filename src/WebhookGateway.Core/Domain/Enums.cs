namespace WebhookGateway.Core.Domain;

// Los valores numéricos se persisten en SQL Server. Nunca se reordenan ni se reutilizan:
// añadir siempre al final. Los nombres van en inglés para coincidir con el esquema.

/// <summary>Estado de un webhook recibido.</summary>
public enum MessageStatus : byte
{
    /// <summary>Recibido y con entregas creadas.</summary>
    Received = 0,

    /// <summary>Ya se había recibido: coincidió con la clave de deduplicación.</summary>
    Duplicate = 1,

    /// <summary>Aceptado, pero ninguna suscripción activa lo reclamó.</summary>
    NoSubscriptions = 2,
}

/// <summary>Estado de una entrega, es decir, de un par (mensaje × destino).</summary>
public enum DeliveryStatus : byte
{
    /// <summary>Esperando su primer intento.</summary>
    Pending = 0,

    /// <summary>Reclamada por un worker. Válida mientras no expire su lease.</summary>
    InFlight = 1,

    /// <summary>Falló de forma recuperable. Volverá a intentarse en <c>NextAttemptAt</c>.</summary>
    Retrying = 2,

    /// <summary>El destino respondió 2xx. Estado final.</summary>
    Delivered = 3,

    /// <summary>Fallo permanente o intentos agotados. Estado final.</summary>
    Failed = 4,

    /// <summary>Se agotó la ventana de entrega sin éxito. Estado final.</summary>
    Expired = 5,

    /// <summary>Cancelada a mano desde el panel. Estado final.</summary>
    Cancelled = 6,

    /// <summary>El circuito de su destino está abierto. El barredor la reactivará.</summary>
    Parked = 7,
}

/// <summary>Autenticación exigida a quien envía webhooks hacia nosotros.</summary>
public enum InboundAuthType : byte
{
    None = 0,
    ApiKey = 1,
    Basic = 2,
    Bearer = 3,
    Hmac = 4,
    IpAllowlist = 5,
}

/// <summary>Autenticación que aplicamos al entregar hacia un destino.</summary>
public enum OutboundAuthType : byte
{
    None = 0,
    ApiKey = 1,
    Basic = 2,
    Bearer = 3,
    Hmac = 4,

    /// <summary>Única estrategia con estado: caché de token, expiración y refresco.</summary>
    OAuth2ClientCredentials = 5,
}

/// <summary>De dónde sale la clave de deduplicación de un mensaje entrante.</summary>
public enum DedupeStrategy : byte
{
    /// <summary>Sin deduplicación. Cada petición es un mensaje nuevo.</summary>
    None = 0,

    /// <summary>Del valor de una cabecera, por ejemplo <c>X-Event-Id</c>.</summary>
    Header = 1,

    /// <summary>De una ruta JSONPath dentro del cuerpo.</summary>
    JsonPath = 2,

    /// <summary>Del hash SHA-256 del cuerpo crudo.</summary>
    BodyHash = 3,
}

/// <summary>Dónde viaja una clave de API.</summary>
public enum ApiKeyLocation : byte
{
    Header = 0,
    QueryString = 1,
}

/// <summary>Algoritmo de firma HMAC.</summary>
public enum HmacAlgorithm : byte
{
    HmacSha256 = 0,
    HmacSha1 = 1,
    HmacSha512 = 2,
}

/// <summary>Dónde espera el servidor de tokens las credenciales del cliente.</summary>
public enum OAuth2CredentialPlacement : byte
{
    /// <summary>Cabecera <c>Authorization: Basic</c>. Lo que dice el RFC 6749.</summary>
    BasicHeader = 0,

    /// <summary>En el cuerpo del formulario. Lo que esperan muchos proveedores reales.</summary>
    RequestBody = 1,
}

/// <summary>Cómo se guardó el cuerpo de un mensaje.</summary>
public enum PayloadEncoding : byte
{
    Raw = 0,
    Gzip = 1,
}

/// <summary>Veredicto sobre un intento de entrega. Decide si se reintenta.</summary>
public enum AttemptVerdict : byte
{
    /// <summary>El destino aceptó. Entrega cerrada.</summary>
    Success = 0,

    /// <summary>Fallo transitorio. Se reintenta con backoff.</summary>
    Retryable = 1,

    /// <summary>El destino rechazó el contenido. Insistir no cambia nada.</summary>
    Permanent = 2,
}
