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
        El claim de un solo destino.

        READPAST salta las filas que otro worker ya tiene bloqueadas en vez de esperarlas y
        UPDLOCK las reserva para esta transacción. Eso es lo que hace que dos instancias vivas
        durante un despliegue no se pisen: sin claim atómico serían entregas duplicadas.

        Antes esto reclamaba un lote global y lo repartía con ROW_NUMBER() PARTITION BY destino,
        que numeraba todo el backlog pendiente en cada ciclo y bloqueaba cada fila que leía. Ahora
        cada destino pide lo suyo: con IX_Delivery_DispatchByEndpoint —(OutboundEndpointId,
        NextAttemptAt), filtrado por Status IN (0, 2)— es una búsqueda directa que no toca ni mira
        las filas de los demás destinos. El reparto justo ya no lo da el SQL, lo da que cada
        destino avance por su cuenta.
    */
    private const string ClaimForEndpointSql = """
        UPDATE d
        SET Status = 1,
            LeaseUntil = @LeaseUntil,
            WorkerId = @WorkerId
        OUTPUT INSERTED.Id, INSERTED.CreatedAt, INSERTED.MessageId,
               INSERTED.OutboundEndpointId, INSERTED.AttemptCount, INSERTED.ExpiresAt
        FROM dbo.WebhookDelivery AS d
        INNER JOIN (
            SELECT TOP (@BatchSize) Id, CreatedAt
            FROM dbo.WebhookDelivery WITH (READPAST, UPDLOCK, ROWLOCK)
            WHERE OutboundEndpointId = @EndpointId
              AND Status IN (0, 2)
              AND NextAttemptAt <= @Now
              AND ExpiresAt > @Now
            ORDER BY NextAttemptAt, Id
        ) AS Pick ON Pick.Id = d.Id AND Pick.CreatedAt = d.CreatedAt;
        """;

    /*
        Qué destinos tienen trabajo vencido. El supervisor lo pregunta cada ciclo para saber a
        quién hay que poner en marcha, así que tiene que ser barato: con el índice filtrado por
        Status IN (0, 2) y OutboundEndpointId como primera clave, el motor recorre un valor
        distinto por destino y no las filas de cada uno.

        Sin UPDLOCK ni READPAST: aquí no se reserva nada, solo se pregunta. Que la respuesta se
        quede corta o larga por un instante no importa, porque el claim de cada destino es el que
        decide de verdad.
    */
    private const string EndpointsWithWorkSql = """
        SELECT DISTINCT OutboundEndpointId
        FROM dbo.WebhookDelivery WITH (NOLOCK)
        WHERE Status IN (0, 2)
          AND NextAttemptAt <= @Now
          AND ExpiresAt > @Now;
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

    /// <summary>Reserva hasta <paramref name="batchSize"/> entregas de un solo destino.</summary>
    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimForEndpointAsync(
        int endpointId, DateTime now, DateTime leaseUntil, string workerId, int batchSize, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<ClaimedDelivery>(new CommandDefinition(
            ClaimForEndpointSql,
            new { EndpointId = endpointId, Now = now, LeaseUntil = leaseUntil, WorkerId = workerId, BatchSize = batchSize },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <summary>
    /// Destinos con entregas vencidas esperando. No reserva nada: es la pregunta que el
    /// supervisor hace para saber a quién poner en marcha.
    /// </summary>
    public async Task<IReadOnlyList<int>> FindEndpointsWithWorkAsync(DateTime now, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<int>(new CommandDefinition(
            EndpointsWithWorkSql, new { Now = now }, cancellationToken: cancellationToken));

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
