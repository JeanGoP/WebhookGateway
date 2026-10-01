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
        La fecha se resuelve dentro de SQL Server y no vuelve a salir: el seek entra por
        UX_WebhookMessage_Id (Id, ReceivedAt) y de ahí salta a la clave agrupada del payload,
        (ReceivedAt, MessageId), sin recorrer particiones.

        Antes el ReceivedAt se leía en una consulta aparte y volvía como parámetro. Eso era el
        fallo: Dapper mandaba el DateTime como `datetime`, cuya rejilla es de 1/300 de segundo,
        y al compararlo con la columna `datetime2(3)` aparecía un .7333333 donde había un
        .7330000. Cero filas y ningún error: el cuerpo existía pero era invisible. Manteniendo
        la comparación entre columnas, el problema no puede volver aunque alguien cambie el
        mapeo de tipos de Dapper.
    */
    private const string Sql = """
        SELECT p.Encoding, p.SizeBytes, p.Body, p.StorageRef, m.HeadersJson
        FROM dbo.WebhookMessage AS m
        INNER JOIN dbo.WebhookPayload AS p
            ON p.ReceivedAt = m.ReceivedAt AND p.MessageId = m.Id
        WHERE m.Id = @MessageId;
        """;

    private const string BatchSql = """
        SELECT m.Id AS MessageId, p.Encoding, p.SizeBytes, p.Body, p.StorageRef, m.HeadersJson
        FROM dbo.WebhookMessage AS m
        INNER JOIN dbo.WebhookPayload AS p
            ON p.ReceivedAt = m.ReceivedAt AND p.MessageId = m.Id
        WHERE m.Id IN @MessageIds;
        """;

    /// <summary>
    /// Devuelve <see langword="null"/> si el mensaje no existe o si su cuerpo ya se purgó. Lo
    /// segundo no es un error: la retención del cuerpo es más corta que la de la metadata a
    /// propósito.
    /// </summary>
    public async Task<OutgoingPayload?> LoadAsync(long messageId, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<PayloadRow>(new CommandDefinition(
            Sql, new { MessageId = messageId }, cancellationToken: cancellationToken));

        if (row is null)
        {
            return null;
        }

        var stored = new StoredPayload((PayloadEncoding)row.Encoding, row.SizeBytes, row.Body, row.StorageRef);
        var body = await payloadStore.LoadAsync(stored, cancellationToken);

        return new OutgoingPayload(body, ExtractContentType(row.HeadersJson));
    }

    /// <summary>
    /// Carga en bloque los cuerpos de una lista de mensajes en una sola consulta SQL.
    /// Evita que N entregas concurrentes abran N conexiones a la base de datos.
    /// </summary>
    public async Task<Dictionary<long, OutgoingPayload>> LoadBatchAsync(IEnumerable<long> messageIds, CancellationToken cancellationToken)
    {
        var ids = messageIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<BatchPayloadRow>(new CommandDefinition(
            BatchSql, new { MessageIds = ids }, cancellationToken: cancellationToken));

        var result = new Dictionary<long, OutgoingPayload>(ids.Length);

        foreach (var row in rows)
        {
            var stored = new StoredPayload((PayloadEncoding)row.Encoding, row.SizeBytes, row.Body, row.StorageRef);
            var body = await payloadStore.LoadAsync(stored, cancellationToken);
            result[row.MessageId] = new OutgoingPayload(body, ExtractContentType(row.HeadersJson));
        }

        return result;
    }

    /// <summary>
    /// Sobrecarga histórica. <paramref name="receivedAt"/> ya no se usa: la fecha se resuelve
    /// dentro de la consulta a partir del identificador del mensaje. Se conserva para no
    /// romper a quien todavía la llame.
    /// </summary>
    public Task<OutgoingPayload?> LoadAsync(long messageId, DateTime receivedAt, CancellationToken cancellationToken) =>
        LoadAsync(messageId, cancellationToken);

    /// <summary>
    /// El tipo de contenido original se reenvía tal cual. Si no llegó ninguno, se asume JSON,
    /// que es lo que manda el 99 % de los emisores.
    /// </summary>
    /// <remarks>
    /// La comparación del nombre es insensible a mayúsculas a propósito: HTTP/2 obliga a
    /// mandar las cabeceras en minúsculas, así que un emisor moderno guarda "content-type" y
    /// una comparación ordinal lo dejaría escapar hasta el valor por defecto.
    /// </remarks>
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
                if (!property.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                    property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = property.Value.GetString();
                return string.IsNullOrWhiteSpace(value) ? DefaultContentType : value;
            }
        }
        catch (JsonException)
        {
            // Cabeceras ilegibles no deben impedir una entrega. Se sigue con el valor por defecto.
        }

        return DefaultContentType;
    }

    /// <summary>Forma cruda de la fila. Dapper necesita propiedades con setter.</summary>
    private class PayloadRow
    {
        public byte Encoding { get; init; }

        public int SizeBytes { get; init; }

        public byte[]? Body { get; init; }

        public string? StorageRef { get; init; }

        public string? HeadersJson { get; init; }
    }

    private sealed class BatchPayloadRow : PayloadRow
    {
        public long MessageId { get; init; }
    }
}
