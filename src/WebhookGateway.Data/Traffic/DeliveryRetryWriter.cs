using Dapper;
using WebhookGateway.Core.Common;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Db;

namespace WebhookGateway.Data.Traffic;

/// <param name="CreatedAt">
/// El <c>ReceivedAt</c> del mensaje, no la hora del reenvío. Es la invariante que permite
/// localizar el cuerpo con un seek sobre la clave agrupada en vez de recorrer particiones.
/// </param>
/// <param name="ExpiresAt">
/// Ventana nueva contada desde ahora. Heredarla del mensaje original haría que un reenvío
/// de algo antiguo naciera expirado.
/// </param>
public sealed record DeliveryRetryResult(
    long OriginalDeliveryId,
    long NewDeliveryId,
    DateTime CreatedAt,
    DateTime ExpiresAt);

/// <summary>
/// Reenvío manual desde el panel: crea una entrega nueva a partir de una ya cerrada.
/// </summary>
/// <remarks>
/// No reabre la entrega original. Su historial de intentos es la prueba de lo que pasó y
/// reescribirlo dejaría al panel mintiendo sobre por qué se reintentó.
/// </remarks>
public sealed class DeliveryRetryWriter(ISqlConnectionFactory connectionFactory, TimeProvider clock)
{
    /*
        El seek por Id se apoya en UX_Delivery_Id (db/06-delivery-by-id.sql). La unión con
        el payload usa d.CreatedAt como ReceivedAt: eso es exactamente lo que compra la
        invariante CreatedAt = ReceivedAt.
    */
    private const string LoadSourceSql = """
        SELECT d.Id, d.MessageId, d.CreatedAt, d.OutboundEndpointId, d.Status,
               o.DeliveryWindowHours, o.IsActive AS EndpointIsActive,
               CASE WHEN p.MessageId IS NULL THEN 0 ELSE 1 END AS PayloadPresent
        FROM dbo.WebhookDelivery AS d
        INNER JOIN dbo.OutboundEndpoint AS o ON o.Id = d.OutboundEndpointId
        LEFT JOIN dbo.WebhookPayload AS p
            ON p.ReceivedAt = d.CreatedAt AND p.MessageId = d.MessageId
        WHERE d.Id = @Id;
        """;

    private const string InsertDeliverySql = """
        INSERT INTO dbo.WebhookDelivery
            (CreatedAt, MessageId, OutboundEndpointId, Status, NextAttemptAt, ExpiresAt)
        OUTPUT INSERTED.Id
        VALUES (@CreatedAt, @MessageId, @OutboundEndpointId, @Status, @NextAttemptAt, @ExpiresAt);
        """;

    public async Task<Result<DeliveryRetryResult>> RetryAsync(long deliveryId, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var source = await connection.QuerySingleOrDefaultAsync<RetrySourceRow>(new CommandDefinition(
            LoadSourceSql, new { Id = deliveryId }, cancellationToken: cancellationToken));

        if (source is null)
        {
            return Result.Fail<DeliveryRetryResult>("delivery.not_found", "Entrega no encontrada.");
        }

        var failure = Validate(source);

        if (failure is not null)
        {
            return Result.Fail<DeliveryRetryResult>(failure.Value);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var expiresAt = now.AddHours(source.DeliveryWindowHours);

        var newId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            InsertDeliverySql,
            new
            {
                source.CreatedAt,
                source.MessageId,
                source.OutboundEndpointId,
                Status = (byte)DeliveryStatus.Pending,
                NextAttemptAt = now,
                ExpiresAt = expiresAt,
            },
            cancellationToken: cancellationToken));

        return Result.Ok(new DeliveryRetryResult(deliveryId, newId, source.CreatedAt, expiresAt));
    }

    /// <summary>Las tres razones por las que un reenvío no puede salir bien.</summary>
    private static Failure? Validate(RetrySourceRow source)
    {
        var status = (DeliveryStatus)source.Status;

        // Reenviar algo que el despachador aún tiene entre manos duplicaría la entrega.
        if (status is not (DeliveryStatus.Delivered or DeliveryStatus.Failed
            or DeliveryStatus.Expired or DeliveryStatus.Cancelled))
        {
            return new Failure("delivery.in_progress",
                $"La entrega sigue en curso ({status}). Espera a que termine antes de reenviarla.");
        }

        // Sin cuerpo no hay nada que mandar: la retención del payload es más corta.
        if (source.PayloadPresent == 0)
        {
            return new Failure("delivery.payload_purged",
                "El cuerpo de este mensaje ya se purgó, así que no puede reenviarse.");
        }

        if (!source.EndpointIsActive)
        {
            return new Failure("delivery.endpoint_inactive",
                "El destino está desactivado. Actívalo antes de reenviar.");
        }

        return null;
    }

    /// <summary>Forma cruda de la fila. Dapper necesita propiedades con setter.</summary>
    private sealed class RetrySourceRow
    {
        public long Id { get; init; }

        public long MessageId { get; init; }

        public DateTime CreatedAt { get; init; }

        public int OutboundEndpointId { get; init; }

        public byte Status { get; init; }

        public int DeliveryWindowHours { get; init; }

        public bool EndpointIsActive { get; init; }

        public int PayloadPresent { get; init; }
    }
}
