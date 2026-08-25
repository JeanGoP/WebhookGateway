using Dapper;
using WebhookGateway.Core.Abstractions;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Db;

namespace WebhookGateway.Data.Traffic;

/// <summary>
/// Persiste lo que llega por <c>/in/*</c>: el mensaje, su cuerpo, la deduplicación y una
/// entrega por cada suscripción activa. Todo en una sola transacción: o se guarda completo,
/// o no se guarda nada y el emisor reintenta.
/// </summary>
public sealed class TrafficWriter(ISqlConnectionFactory connectionFactory)
{
    private const string CheckDedupeSql = """
        SELECT MessageId FROM dbo.MessageDedupe WITH (UPDLOCK, HOLDLOCK)
        WHERE InboundEndpointId = @InboundEndpointId AND DedupeKey = @DedupeKey;
        """;

    private const string InsertMessageSql = """
        INSERT INTO dbo.WebhookMessage
            (ReceivedAt, InboundEndpointId, SourceIp, HttpMethod, HeadersJson, QueryString, BodySizeBytes, BodyHash, Status)
        OUTPUT INSERTED.Id
        VALUES (@ReceivedAt, @InboundEndpointId, @SourceIp, @HttpMethod, @HeadersJson, @QueryString, @BodySizeBytes, @BodyHash, @Status);
        """;

    private const string InsertPayloadSql = """
        INSERT INTO dbo.WebhookPayload (MessageId, ReceivedAt, Encoding, SizeBytes, Body, StorageRef)
        VALUES (@MessageId, @ReceivedAt, @Encoding, @SizeBytes, @Body, @StorageRef);
        """;

    private const string InsertDedupeSql = """
        INSERT INTO dbo.MessageDedupe (InboundEndpointId, DedupeKey, MessageId, ExpiresAt)
        VALUES (@InboundEndpointId, @DedupeKey, @MessageId, @ExpiresAt);
        """;

    private const string InsertDeliverySql = """
        INSERT INTO dbo.WebhookDelivery (CreatedAt, MessageId, OutboundEndpointId, Status, NextAttemptAt, ExpiresAt)
        OUTPUT INSERTED.Id
        VALUES (@CreatedAt, @MessageId, @OutboundEndpointId, @Status, @NextAttemptAt, @ExpiresAt);
        """;

    public async Task<TrafficWriteResult> WriteAsync(TrafficWriteRequest request, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        long? existingMessageId = request.DedupeKey is null
            ? null
            : await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                CheckDedupeSql,
                new { request.InboundEndpointId, DedupeKey = request.DedupeKey },
                transaction,
                cancellationToken: cancellationToken));

        var isDuplicate = existingMessageId is not null;
        var status = isDuplicate ? MessageStatus.Duplicate
            : request.Deliveries.Count == 0 ? MessageStatus.NoSubscriptions
            : MessageStatus.Received;

        var messageId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            InsertMessageSql,
            new
            {
                request.ReceivedAt, request.InboundEndpointId, request.SourceIp, request.HttpMethod,
                request.HeadersJson, request.QueryString, request.BodySizeBytes, request.BodyHash,
                Status = (byte)status,
            },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            InsertPayloadSql,
            new
            {
                MessageId = messageId, request.ReceivedAt, Encoding = (byte)request.Payload.Encoding,
                request.Payload.SizeBytes, request.Payload.Body, request.Payload.StorageRef,
            },
            transaction,
            cancellationToken: cancellationToken));

        if (!isDuplicate && request.DedupeKey is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                InsertDedupeSql,
                new
                {
                    request.InboundEndpointId, DedupeKey = request.DedupeKey, MessageId = messageId,
                    ExpiresAt = request.DedupeExpiresAt,
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        var deliveryIds = new List<long>(isDuplicate ? 0 : request.Deliveries.Count);

        if (!isDuplicate)
        {
            foreach (var delivery in request.Deliveries)
            {
                var deliveryId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    InsertDeliverySql,
                    new
                    {
                        CreatedAt = request.ReceivedAt, MessageId = messageId, delivery.OutboundEndpointId,
                        Status = (byte)DeliveryStatus.Pending, NextAttemptAt = request.ReceivedAt,
                        ExpiresAt = request.ReceivedAt.AddHours(delivery.DeliveryWindowHours),
                    },
                    transaction,
                    cancellationToken: cancellationToken));

                deliveryIds.Add(deliveryId);
            }
        }

        transaction.Commit();

        return new TrafficWriteResult(messageId, status, deliveryIds, existingMessageId);
    }
}

/// <param name="Payload">
/// El cuerpo ya procesado por <see cref="IPayloadStore"/>. Se comprime antes de abrir la
/// transacción: no tiene sentido tener una fila bloqueada mientras se hace CPU-bound work.
/// </param>
public sealed record TrafficWriteRequest(
    int InboundEndpointId,
    DateTime ReceivedAt,
    string SourceIp,
    string HttpMethod,
    string HeadersJson,
    string? QueryString,
    int BodySizeBytes,
    byte[] BodyHash,
    string? DedupeKey,
    DateTime DedupeExpiresAt,
    StoredPayload Payload,
    IReadOnlyList<DeliveryTarget> Deliveries);

public sealed record DeliveryTarget(int OutboundEndpointId, int DeliveryWindowHours);

/// <param name="OriginalMessageId">
/// Id del primer mensaje que usó esta clave de deduplicación, cuando <paramref name="Status"/>
/// es <see cref="MessageStatus.Duplicate"/>. Nulo en cualquier otro caso.
/// </param>
public sealed record TrafficWriteResult(long MessageId, MessageStatus Status, IReadOnlyList<long> DeliveryIds, long? OriginalMessageId);
