using System.Security.Cryptography;
using System.Text.Json;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Core.Reception;

/// <summary>
/// Calcula la clave de deduplicación de un mensaje entrante según la estrategia del
/// endpoint. Devuelve <see langword="null"/> cuando no aplica o la fuente no está presente:
/// un mensaje sin clave verificable nunca se trata como duplicado.
/// </summary>
public static class DedupeKeyExtractor
{
    public static string? Extract(
        DedupeStrategy strategy,
        string? source,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlySpan<byte> body) => strategy switch
        {
            DedupeStrategy.None => null,
            DedupeStrategy.Header => source is not null && headers.TryGetValue(source, out var value) ? value : null,
            DedupeStrategy.JsonPath => source is not null ? ExtractJsonPath(source, body) : null,
            DedupeStrategy.BodyHash => Convert.ToHexStringLower(SHA256.HashData(body)),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Estrategia de deduplicación desconocida."),
        };

    /// <summary>
    /// Solo rutas simples separadas por puntos (<c>evento.id</c>), no JSONPath completo: es
    /// lo único que necesitan los emisores reales, y evita traer una librería para un caso
    /// de uso que no la necesita.
    /// </summary>
    private static string? ExtractJsonPath(string path, ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            using var doc = JsonDocument.ParseValue(ref reader);
            var current = doc.RootElement;

            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
