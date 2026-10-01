using System.Data;
using Dapper;
using WebhookGateway.Core.Abstractions;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Db;

namespace WebhookGateway.Data.Traffic;

/// <summary>
/// Persiste lo que llega por <c>/in/*</c>: el mensaje, su cuerpo, la deduplicación y las
/// entregas en un solo viaje de red mediante un Stored Procedure atómico con deduplicación optimista.
/// </summary>
public sealed class TrafficWriter(ISqlConnectionFactory connectionFactory)
{
    private const string SpName = "dbo.sp_Traffic_WriteInbound";

    public async Task<TrafficWriteResult> WriteAsync(TrafficWriteRequest request, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
            SpName,
            new
            {
                request.InboundEndpointId,
                request.ReceivedAt,
                request.SourceIp,
                request.HttpMethod,
                request.HeadersJson,
                request.QueryString,
                request.BodySizeBytes,
                request.BodyHash,
                request.DedupeKey,
                request.DedupeExpiresAt,
                PayloadEncoding = (byte)request.Payload.Encoding,
                PayloadSizeBytes = request.Payload.SizeBytes,
                PayloadBody = request.Payload.Body,
                PayloadStorageRef = request.Payload.StorageRef,
            },
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        var header = await multi.ReadSingleAsync<WriteHeaderRow>();
        var deliveryIds = (await multi.ReadAsync<long>()).ToList();

        return new TrafficWriteResult(header.MessageId, (MessageStatus)header.Status, deliveryIds, header.ExistingMessageId);
    }

    private sealed record WriteHeaderRow(long MessageId, byte Status, long? ExistingMessageId);
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
