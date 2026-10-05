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

        Cada destino pide lo suyo con IX_Delivery_DispatchByEndpoint —(OutboundEndpointId,
        NextAttemptAt), filtrado por Status IN (0, 2)—, una búsqueda que no toca las filas de los
        demás destinos.

        Sin ORDER BY, y es a propósito. Con él, el claim leía TODO lo vencido del destino para
        quedarse con veinte: el índice está partido por mes y el motor no puede sacar ese orden de
        él, así que lo ordenaba entero. Con un destino caído unas horas eso son cien mil filas
        leídas por claim, y como UPDLOCK bloquea cada fila leída, pasadas unas 5.000 SQL Server
        escala a un bloqueo de tabla que frena también los INSERT de la recepción. Sin ORDER BY el
        TOP para en cuanto tiene sus veinte: lee y bloquea lo que reclama, haya diez pendientes o
        un millón. El orden que queda —por mes y, dentro del mes, por NextAttemptAt— sirve primero
        lo más vencido en la práctica, aunque no lo garantiza; el orden de entrega nunca se
        garantizó, porque cada destino envía varias a la vez.

        El índice va nombrado, y no es desconfianza gratuita: medido con 300.000 pendientes, el
        optimizador elegía IX_Delivery_Backlog —(OutboundEndpointId, Status)— y miraba en la tabla
        la fecha de cada fila. Con un destino lleno de reintentos programados para más tarde eso
        vuelve a ser leer y bloquear todo su backlog. Si el índice no existe, la consulta falla con
        un error claro en vez de volverse lenta en silencio: db/11 va antes que este código.
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
            FROM dbo.WebhookDelivery WITH (READPAST, UPDLOCK, ROWLOCK, INDEX(IX_Delivery_DispatchByEndpoint))
            WHERE OutboundEndpointId = @EndpointId
              AND Status IN (0, 2)
              AND NextAttemptAt <= @Now
              AND ExpiresAt > @Now
        ) AS Pick ON Pick.Id = d.Id AND Pick.CreatedAt = d.CreatedAt;
        """;

    /*
        Qué destinos tienen trabajo vencido. El supervisor lo pregunta cada vez que llega algo,
        hasta cuatro veces por segundo, así que tiene que costar lo mismo con diez pendientes que
        con un millón.

        Por eso va destino por destino: para cada uno, una búsqueda en el índice que para en la
        primera fila vencida. Antes era un SELECT DISTINCT sobre todo lo pendiente, que con un
        destino caído recorría su backlog entero en cada pasada. Los destinos son decenas; las
        filas pendientes pueden ser cientos de miles.

        No se filtra por IsActive: un destino desactivado con entregas pendientes también tiene que
        aparecer, para que su bomba las cierre como "destino desactivado" en vez de dejarlas
        esperando a caducar. Una entrega cuyo destino se borró de la tabla no aparece; la caduca
        el mantenimiento al vencer su ventana.

        El índice va nombrado por lo mismo que en el claim, y aquí se midió peor: con TOP (1) el
        optimizador supone que la primera fila que mire servirá y recorre la tabla agrupada. Para
        el destino con el backlog acierta enseguida; para cualquier otro, atraviesa el backlog
        entero buscando la suya. Con 300.000 pendientes, unas mil páginas por pasada.

        Sin UPDLOCK ni READPAST: aquí no se reserva nada, solo se pregunta. Que la respuesta se
        quede corta o larga por un instante no importa, porque el claim de cada destino es el que
        decide de verdad.
    */
    private const string EndpointsWithWorkSql = """
        SELECT e.Id
        FROM dbo.OutboundEndpoint AS e
        CROSS APPLY (
            SELECT TOP (1) 1 AS HasWork
            FROM dbo.WebhookDelivery AS d WITH (NOLOCK, INDEX(IX_Delivery_DispatchByEndpoint))
            WHERE d.OutboundEndpointId = e.Id
              AND d.Status IN (0, 2)
              AND d.NextAttemptAt <= @Now
              AND d.ExpiresAt > @Now
        ) AS w;
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
