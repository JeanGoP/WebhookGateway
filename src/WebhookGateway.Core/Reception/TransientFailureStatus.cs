namespace WebhookGateway.Core.Reception;

/// <summary>
/// Los códigos con los que se puede rechazar un webhook que no se pudo guardar.
/// </summary>
/// <remarks>
/// Solo los que un emisor entiende como "vuelve a intentarlo": el fallo es nuestro y pasajero, y lo
/// único que importa es que el emisor reintente. Casi todos reintentan un 503; las apps del
/// Marketplace de GHL, solo un 429. Un 4xx distinto de 429 diría que el problema es del mensaje, y
/// el emisor lo daría por perdido.
/// </remarks>
public static class TransientFailureStatus
{
    public const int Default = 503;

    public static readonly IReadOnlyList<int> Allowed = [429, 500, 502, 503, 504];

    public static bool IsAllowed(int statusCode) => Allowed.Contains(statusCode);
}
