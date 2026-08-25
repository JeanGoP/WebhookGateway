using Dapper;
using System.Text.Json;
using WebhookGateway.Core.Abstractions;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Db;

namespace WebhookGateway.Data.Traffic;

/// <summary>El cuerpo listo para reenviar, tal como llegó.</summary>
public sealed record OutgoingPayload(ReadOnlyMemory<byte> Body, string ContentType);

/// <summary>
/// Recupera el cuerpo de un mensaje para entregarlo. Se lee en el momento del envío y no se
/// mantiene en memoria: así una ráfaga de 2.000 mensajes no se traduce en presión de memoria.
/// </summary>
public sealed class MessagePayloadReader(ISqlConnectionFactory connectionFactory, IPayloadStore payloadStore)
{
    private const string DefaultContentType = "application/json";

    /*
        Se busca por (ReceivedAt, MessageId), que es la clave agrupada de las dos tablas.
        Buscar solo por MessageId obligaría a recorrer todas las particiones.
    */
    private const string Sql = """
        SELECT p.Encoding, p.SizeBytes, p.Body, p.StorageRef, m.HeadersJson
        FROM dbo.WebhookPayload AS p
        INNER JOIN dbo.WebhookMessage AS m
            ON m.ReceivedAt = p.ReceivedAt AND m.Id = p.MessageId
        WHERE p.ReceivedAt = @ReceivedAt AND p.MessageId = @MessageId;
        """;

    /// <summary>
    /// Devuelve <see langword="null"/> si el cuerpo ya se purgó. No es un error: la retención
    /// del cuerpo es más corta que la de la metadata a propósito.
    /// </summary>
    public async Task<OutgoingPayload?> LoadAsync(long messageId, DateTime receivedAt, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<PayloadRow>(new CommandDefinition(
            Sql, new { ReceivedAt = receivedAt, MessageId = messageId }, cancellationToken: cancellationToken));

        if (row is null)
        {
            return null;
        }

        var stored = new StoredPayload((PayloadEncoding)row.Encoding, row.SizeBytes, row.Body, row.StorageRef);
        var body = await payloadStore.LoadAsync(stored, cancellationToken);

        return new OutgoingPayload(body, ExtractContentType(row.HeadersJson));
    }

    /// <summary>
    /// El tipo de contenido original se reenvía tal cual. Si no llegó ninguno, se asume JSON,
    /// que es lo que manda el 99 % de los emisores.
    /// </summary>
    private static string ExtractContentType(string? headersJson)
    {
        if (string.IsNullOrEmpty(headersJson))
        {
            return DefaultContentType;
        }

        try
        {
            using var document = JsonDocument.Parse(headersJson);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("Content-Type") && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    return string.IsNullOrWhiteSpace(value) ? DefaultContentType : value;
                }
            }
        }
        catch (JsonException)
        {
            // Cabeceras ilegibles no deben impedir una entrega. Se sigue con el valor por defecto.
        }

        return DefaultContentType;
    }

    /// <summary>Forma cruda de la fila. Dapper necesita propiedades con setter.</summary>
    private sealed class PayloadRow
    {
        public byte Encoding { get; init; }

        public int SizeBytes { get; init; }

        public byte[]? Body { get; init; }

        public string? StorageRef { get; init; }

        public string? HeadersJson { get; init; }
    }
}
