namespace WebhookGateway.Core.Reception;

/// <summary>
/// Enmascara cabeceras sensibles antes de guardarlas. Nada que huela a credencial se
/// persiste en claro, ni siquiera en la tabla de mensajes.
/// </summary>
public static class HeaderMasking
{
    private static readonly string[] SensitiveMarkers =
        ["authorization", "cookie", "secret", "token", "signature", "api-key", "apikey", "password"];

    public static IReadOnlyDictionary<string, string> Mask(IReadOnlyDictionary<string, string> headers)
    {
        var masked = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in headers)
        {
            masked[name] = IsSensitive(name) ? "***" : value;
        }

        return masked;
    }

    private static bool IsSensitive(string headerName)
    {
        foreach (var marker in SensitiveMarkers)
        {
            if (headerName.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
