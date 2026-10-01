namespace WebhookGateway.Core.Notifications;

/// <summary>Estado de una notificación en la Outbox.</summary>
public enum NotificationStatus : byte
{
    /// <summary>Esperando su primer intento de envío.</summary>
    Pending = 0,

    /// <summary>Reclamada por un worker de notificaciones.</summary>
    InFlight = 1,

    /// <summary>Falló de forma recuperable. Volverá a intentarse en NextAttemptAt.</summary>
    Retrying = 2,

    /// <summary>Se agotaron los intentos o fallo permanente. Estado final.</summary>
    DeadLetter = 3,
}

/// <summary>Tipo de alerta generada por el sistema.</summary>
public enum NotificationAlertType : byte
{
    /// <summary>Un evento agotó todos sus reintentos o falló permanentemente.</summary>
    DeadLetter = 1,

    /// <summary>El endpoint destino presenta fallos intermitentes.</summary>
    EndpointDegraded = 2,

    /// <summary>El cortacircuitos se abrió: el destino está caído.</summary>
    EndpointDown = 3,

    /// <summary>El destino volvió a responder con 2xx.</summary>
    EndpointRecovered = 4,

    /// <summary>Error en recepción tipificado (ej. JSON malformado de un emisor autenticado).</summary>
    InboundError = 5,

    /// <summary>Correo de verificación doble opt-in para un nuevo suscriptor.</summary>
    Verification = 6,
}

/// <summary>Canal de despacho de la notificación.</summary>
public enum NotificationChannel : byte
{
    Email = 0,
    Webhook = 1,
}

/// <summary>Resultado de entrega persistido en NotificationLog.</summary>
public enum NotificationLogStatus : byte
{
    Delivered = 0,
    BouncedOrRejected = 1,
    FailedPermanent = 2,
}

/// <summary>Estado de salud de un endpoint de destino.</summary>
public enum EndpointHealthStatus : byte
{
    /// <summary>Respondiendo con normalidad (2xx).</summary>
    Healthy = 0,

    /// <summary>Presentando fallos intermitentes consecutivos.</summary>
    Degraded = 1,

    /// <summary>Circuito abierto: destino caído tras alcanzar umbral de fallos.</summary>
    Down = 2,

    /// <summary>Recuperado tras haber estado caído o degradado.</summary>
    Recovered = 3,
}

/// <summary>Estado del ciclo de vida y verificación de un suscriptor de alertas.</summary>
public enum SubscriberStatus : byte
{
    /// <summary>Registrado pero pendiente de hacer clic en el correo de confirmación.</summary>
    PendingVerification = 0,

    /// <summary>Confirmado mediante doble opt-in: recibe alertas.</summary>
    Verified = 1,

    /// <summary>Silenciado temporalmente (ej. por mantenimiento programado).</summary>
    Silenced = 2,

    /// <summary>Se dio de baja mediante el enlace de un solo clic.</summary>
    Unsubscribed = 3,
}
