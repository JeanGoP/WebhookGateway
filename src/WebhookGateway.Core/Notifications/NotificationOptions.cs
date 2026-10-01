namespace WebhookGateway.Core.Notifications;

/// <summary>Configuración general del subsistema de notificaciones y alertas.</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Gateway:Notifications";

    /// <summary>Interruptor maestro para activar o desactivar el envío de alertas.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Correo del administrador del sistema que recibe copia de los incidentes.</summary>
    public string AdminEmail { get; set; } = string.Empty;

    /// <summary>URL base del panel web para generar enlaces directos a los incidentes.</summary>
    public string DashboardBaseUrl { get; set; } = "http://localhost:5174";

    /// <summary>URL base pública del API para enlaces directos de verificación y desuscripción.</summary>
    public string ApiBaseUrl { get; set; } = "https://localhost:7004";

    /// <summary>Ajustes de conexión con el proveedor SMTP (Turbo SMTP).</summary>
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>Ajustes del worker que drena la NotificationOutbox.</summary>
    public NotificationDrainerOptions Drainer { get; set; } = new();

    /// <summary>
    /// Minutos entre dos avisos de entrega descartada del mismo destino.
    /// </summary>
    /// <remarks>
    /// Sin esto, un destino que responde 400 a todo genera un correo por cada entrega descartada y
    /// por cada destinatario: miles. Dentro de la ventana no se calla y punto, se cuenta, y el
    /// siguiente aviso dice cuántas más hubo. Poner 0 desactiva la agrupación, que es lo que hacía
    /// antes.
    /// <para>
    /// La ventana se lleva en memoria del proceso. Durante un despliegue hay dos instancias vivas y
    /// pueden salir dos avisos en vez de uno; dos en lugar de miles es exactamente el objetivo.
    /// </para>
    /// </remarks>
    public int DeadLetterGroupingMinutes { get; set; } = 15;
}

/// <summary>Parámetros del servidor SMTP.</summary>
public sealed class SmtpOptions
{
    private string _username = string.Empty;
    private string _fromEmail = string.Empty;

    public string Host { get; set; } = "pro.turbo-smtp.com";

    public int Port { get; set; } = 587;

    public bool UseTls { get; set; } = true;

    /// <summary>Alias compatible con configuraciones tipo SslMail.</summary>
    public bool SslMail { get => !UseTls; set => UseTls = !value; }

    /// <summary>Usuario de autenticación en Turbo SMTP.</summary>
    public string User { get; set; } = string.Empty;

    public string Username
    {
        get => string.IsNullOrEmpty(_username) ? User : _username;
        set => _username = value;
    }

    public string Password { get; set; } = string.Empty;

    /// <summary>Correo remitente oficial (From).</summary>
    public string From { get; set; } = string.Empty;

    public string FromEmail
    {
        get => string.IsNullOrEmpty(_fromEmail) ? From : _fromEmail;
        set => _fromEmail = value;
    }

    public string FromName { get; set; } = "Webhook Gateway";

    /// <summary>
    /// Si está en true, no se conecta a Turbo SMTP; imprime el correo en consola/log y simula éxito.
    /// Ideal para desarrollo local sin consumir cuota.
    /// </summary>
    public bool DryRun { get; set; }
}

/// <summary>Parámetros de ritmo del worker drenador.</summary>
public sealed class NotificationDrainerOptions
{
    /// <summary>Intervalo de sondeo en segundos cuando la outbox está vacía.</summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>Cantidad máxima de notificaciones reclamadas por ciclo.</summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>Tiempo de retención del lease sobre una notificación en vuelo.</summary>
    public int LeaseSeconds { get; set; } = 60;

    /// <summary>Intentos máximos de reintento ante fallos temporales de SMTP.</summary>
    public int MaxDeliveryAttempts { get; set; } = 5;
}
