using Dapper;
using WebhookGateway.Data.Db;

namespace WebhookGateway.Data.Traffic;

/// <summary>
/// Consultas de solo lectura para el explorador de mensajes del panel. Dapper puro: las
/// tablas de tráfico no están en EF, y las consultas necesitan control exacto para usar
/// los índices filtrados.
/// </summary>
public sealed class MessageExplorer(ISqlConnectionFactory connectionFactory)
{
    /*
        Consulta con filtros opcionales, el patrón «@X IS NULL OR columna = @X».

        Necesita OPTION (RECOMPILE), y no es un adorno: sin él, SQL Server guarda en caché el plan que
        construyó para el PRIMER juego de parámetros que le toque y lo reutiliza para todos los demás.
        El plan de "dame los últimos 50 de todas las integraciones" no sirve para "los del endpoint 7
        del martes pasado", y el buscador es la mitad del valor del producto: el que se abre cuando
        algo fue mal y hay que encontrar un mensaje entre millones.

        Con RECOMPILE, el motor planifica con los valores reales en la mano y además descarta los
        predicados que no aplican en esta llamada, de modo que puede usar IX_WebhookMessage_Search
        —(InboundEndpointId, ReceivedAt DESC)— cuando se filtra por endpoint. El coste es compilar en
        cada búsqueda: microsegundos de CPU para una consulta que la lanza una persona mirando una
        pantalla, no el camino caliente.
    */
    private const string SearchMessagesSql = """
        SELECT TOP (@PageSize)
            m.Id, m.ReceivedAt, m.InboundEndpointId, m.SourceIp,
            m.HttpMethod, m.BodySizeBytes, m.Status,
            e.Name AS EndpointName, i.Name AS IntegrationName
        FROM dbo.WebhookMessage AS m
        INNER JOIN dbo.InboundEndpoint AS e ON e.Id = m.InboundEndpointId
        INNER JOIN dbo.Integration AS i ON i.Id = e.IntegrationId
        WHERE (@IntegrationId IS NULL OR e.IntegrationId = @IntegrationId)
          AND (@InboundEndpointId IS NULL OR m.InboundEndpointId = @InboundEndpointId)
          AND (@Status IS NULL OR m.Status = @Status)
          AND (@From IS NULL OR m.ReceivedAt >= @From)
          AND (@To IS NULL OR m.ReceivedAt < @To)
          AND (@AfterId IS NULL OR m.Id < @AfterId)
        ORDER BY m.ReceivedAt DESC, m.Id DESC
        OPTION (RECOMPILE);
        """;

    private const string GetMessageSql = """
        SELECT m.Id, m.ReceivedAt, m.InboundEndpointId, m.SourceIp,
               m.HttpMethod, m.HeadersJson, m.QueryString, m.BodySizeBytes, m.Status,
               e.Name AS EndpointName, e.Slug AS EndpointSlug,
               i.Name AS IntegrationName, i.Slug AS IntegrationSlug
        FROM dbo.WebhookMessage AS m
        INNER JOIN dbo.InboundEndpoint AS e ON e.Id = m.InboundEndpointId
        INNER JOIN dbo.Integration AS i ON i.Id = e.IntegrationId
        WHERE m.Id = @Id;
        """;

    private const string GetDeliveriesSql = """
        SELECT d.Id, d.CreatedAt, d.OutboundEndpointId, d.Status,
               d.AttemptCount, d.NextAttemptAt, d.ExpiresAt,
               d.LastStatusCode, d.LastError, d.CompletedAt,
               o.Name AS EndpointName, o.TargetUrl, o.HttpMethod
        FROM dbo.WebhookDelivery AS d
        INNER JOIN dbo.OutboundEndpoint AS o ON o.Id = d.OutboundEndpointId
        WHERE d.MessageId = @MessageId
        ORDER BY d.Id;
        """;

    private const string GetAttemptsSql = """
        SELECT a.Id, a.StartedAt, a.AttemptNumber, a.DurationMs,
               a.StatusCode, a.ResponseHeadersJson, a.ResponseBody,
               a.ErrorMessage, a.WorkerId
        FROM dbo.DeliveryAttempt AS a
        WHERE a.DeliveryId = @DeliveryId
        ORDER BY a.AttemptNumber;
        """;

    public async Task<IReadOnlyList<MessageSummary>> SearchAsync(MessageSearchQuery query, CancellationToken ct)
    {
        using var connection = await connectionFactory.OpenAsync(ct);

        var rows = await connection.QueryAsync<MessageSummary>(new CommandDefinition(
            SearchMessagesSql,
            new
            {
                query.IntegrationId,
                query.InboundEndpointId,
                Status = (byte?)query.Status,
                query.From,
                query.To,
                query.AfterId,
                PageSize = query.PageSize ?? 50,
            },
            cancellationToken: ct));

        return [.. rows];
    }

    public async Task<MessageDetail?> GetMessageAsync(long id, CancellationToken ct)
    {
        using var connection = await connectionFactory.OpenAsync(ct);

        return await connection.QuerySingleOrDefaultAsync<MessageDetail>(new CommandDefinition(
            GetMessageSql, new { Id = id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DeliverySummary>> GetDeliveriesAsync(long messageId, CancellationToken ct)
    {
        using var connection = await connectionFactory.OpenAsync(ct);

        var rows = await connection.QueryAsync<DeliverySummary>(new CommandDefinition(
            GetDeliveriesSql, new { MessageId = messageId }, cancellationToken: ct));

        return [.. rows];
    }

    public async Task<IReadOnlyList<AttemptDetail>> GetAttemptsAsync(long deliveryId, CancellationToken ct)
    {
        using var connection = await connectionFactory.OpenAsync(ct);

        var rows = await connection.QueryAsync<AttemptDetail>(new CommandDefinition(
            GetAttemptsSql, new { DeliveryId = deliveryId }, cancellationToken: ct));

        return [.. rows];
    }
}

// --- Query ---

public sealed record MessageSearchQuery(
    int? IntegrationId,
    int? InboundEndpointId,
    byte? Status,
    DateTime? From,
    DateTime? To,
    long? AfterId,
    int? PageSize);

// --- Resultados ---

public sealed class MessageSummary
{
    public long Id { get; init; }
    public DateTime ReceivedAt { get; init; }
    public int InboundEndpointId { get; init; }
    public string SourceIp { get; init; } = "";
    public string HttpMethod { get; init; } = "";
    public int BodySizeBytes { get; init; }
    public byte Status { get; init; }
    public string EndpointName { get; init; } = "";
    public string IntegrationName { get; init; } = "";
}

public sealed class MessageDetail
{
    public long Id { get; init; }
    public DateTime ReceivedAt { get; init; }
    public int InboundEndpointId { get; init; }
    public string SourceIp { get; init; } = "";
    public string HttpMethod { get; init; } = "";
    public string? HeadersJson { get; init; }
    public string? QueryString { get; init; }
    public int BodySizeBytes { get; init; }
    public byte Status { get; init; }
    public string EndpointName { get; init; } = "";
    public string IntegrationName { get; init; } = "";
    public string EndpointSlug { get; init; } = "";
    public string IntegrationSlug { get; init; } = "";
}

public sealed class DeliverySummary
{
    public long Id { get; init; }
    public DateTime CreatedAt { get; init; }
    public int OutboundEndpointId { get; init; }
    public byte Status { get; init; }
    public short AttemptCount { get; init; }
    public DateTime NextAttemptAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public short? LastStatusCode { get; init; }
    public string? LastError { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string EndpointName { get; init; } = "";
    public string TargetUrl { get; init; } = "";
    public string HttpMethod { get; init; } = "POST";
}

public sealed class AttemptDetail
{
    public long Id { get; init; }
    public DateTime StartedAt { get; init; }
    public short AttemptNumber { get; init; }
    public int DurationMs { get; init; }
    public short? StatusCode { get; init; }
    public string? ResponseHeadersJson { get; init; }
    public string? ResponseBody { get; init; }
    public string? ErrorMessage { get; init; }
    public string? WorkerId { get; init; }
}
