using Dapper;
using WebhookGateway.Data.Db;

namespace WebhookGateway.Dispatcher.Claiming;

/// <summary>Una entrega reclamada por este worker, con lo justo para poder enviarla.</summary>
/// <param name="CreatedAt">
/// Clave de partición de la entrega y, por construcción, también la de su mensaje: la
/// recepción las escribe con el mismo instante. De ahí sale el cuerpo sin escanear
/// particiones. Si algún día el reenvío manual crea entregas nuevas, tiene que respetar
/// esta invariante.
/// </param>
public sealed record ClaimedDelivery(
    long Id,
    DateTime CreatedAt,
    long MessageId,
    int OutboundEndpointId,
    short AttemptCount,
    DateTime ExpiresAt);

/// <summary>
/// El reparto de trabajo entre workers. Todo lo de aquí es SQL exacto por una razón: durante
/// un despliegue hay dos instancias vivas a la vez, y sin un claim atómico eso son entregas
/// duplicadas en producción.
/// </summary>
public sealed class DeliveryClaimer(ISqlConnectionFactory connectionFactory)
{
    /*
        READPAST salta las filas que otro worker ya tiene bloqueadas en vez de esperarlas,
        UPDLOCK las reserva para esta transacción, y el ROW_NUMBER por destino garantiza que
        un backlog concentrado en un destino no se lleve el lote entero.

        El índice IX_Delivery_Dispatch está filtrado por Status IN (0, 2), así que esta
        consulta solo recorre lo que está pendiente: cuesta lo mismo con la tabla vacía que
        con quince millones de filas.
    */
    private const string ClaimSql = """
        UPDATE d
        SET Status = 1,
            LeaseUntil = @LeaseUntil,
            WorkerId = @WorkerId
        OUTPUT INSERTED.Id, INSERTED.CreatedAt, INSERTED.MessageId,
               INSERTED.OutboundEndpointId, INSERTED.AttemptCount, INSERTED.ExpiresAt
        FROM dbo.WebhookDelivery AS d
        INNER JOIN (
            SELECT TOP (@BatchSize) Id, CreatedAt
            FROM (
                SELECT Id, CreatedAt,
                       ROW_NUMBER() OVER (PARTITION BY OutboundEndpointId ORDER BY NextAttemptAt, Id) AS Rn
                FROM dbo.WebhookDelivery WITH (READPAST, UPDLOCK, ROWLOCK)
                WHERE Status IN (0, 2)
                  AND NextAttemptAt <= @Now
                  AND ExpiresAt > @Now
            ) AS Ranked
            WHERE Ranked.Rn <= @PerEndpoint
            ORDER BY Ranked.Rn, Ranked.Id
        ) AS Pick ON Pick.Id = d.Id AND Pick.CreatedAt = d.CreatedAt;
        """;

    /* Una instancia que muere deja su lease colgado. Al vencer, la entrega vuelve a la cola. */
    private const string RecoverLeasesSql = """
        UPDATE TOP (@Limit) dbo.WebhookDelivery
        SET Status = 2, LeaseUntil = NULL, WorkerId = NULL
        WHERE Status = 1 AND LeaseUntil < @Now;
        """;

    /* Pasada la ventana, reintentar es trabajo que nadie va a aprovechar. */
    private const string ExpireSql = """
        UPDATE TOP (@Limit) dbo.WebhookDelivery
        SET Status = 5, CompletedAt = @Now, LeaseUntil = NULL, WorkerId = NULL
        WHERE Status IN (0, 2) AND ExpiresAt <= @Now;
        """;

    /* Apagado ordenado: lo que este worker no llegó a enviar vuelve a la cola de inmediato. */
    private const string ReleaseSql = """
        UPDATE dbo.WebhookDelivery
        SET Status = 2, LeaseUntil = NULL, WorkerId = NULL
        WHERE Status = 1 AND WorkerId = @WorkerId;
        """;

    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(
        DateTime now, DateTime leaseUntil, string workerId, int batchSize, int perEndpoint, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<ClaimedDelivery>(new CommandDefinition(
            ClaimSql,
            new { Now = now, LeaseUntil = leaseUntil, WorkerId = workerId, BatchSize = batchSize, PerEndpoint = perEndpoint },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<int> RecoverOrphanedLeasesAsync(DateTime now, int limit, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteAsync(new CommandDefinition(
            RecoverLeasesSql, new { Now = now, Limit = limit }, cancellationToken: cancellationToken));
    }

    public async Task<int> ExpireOverdueAsync(DateTime now, int limit, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteAsync(new CommandDefinition(
            ExpireSql, new { Now = now, Limit = limit }, cancellationToken: cancellationToken));
    }

    public async Task<int> ReleaseAllAsync(string workerId, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteAsync(new CommandDefinition(
            ReleaseSql, new { WorkerId = workerId }, cancellationToken: cancellationToken));
    }
}
