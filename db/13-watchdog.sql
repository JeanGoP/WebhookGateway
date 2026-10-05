/*
    WebhookGateway — vigilante. Idempotente.

    Comprueba lo que, si pasa desapercibido, se convierte en pérdida de mensajes:

      1. Que no se acaben las particiones futuras. Si se acaban, los INSERT de tráfico empiezan a
         fallar y la recepción devuelve 503 a todo el mundo. Es el fallo más grave posible aquí y el
         más fácil de evitar, porque se ve venir con meses de antelación.
      2. Que el backlog de entregas no crezca sin parar. Un backlog que sube y no baja significa que
         el despachador no da el ritmo o que un destino está caído.
      3. Que ninguna entrega lleve esperando más de lo acordado (15 minutos por defecto). Es la
         cifra que de verdad dice si vamos al día: un backlog grande que se vacía a tiempo no es un
         problema, y uno pequeño que no avanza sí.
      4. Que no queden leases huérfanos.

    También define dbo.fn_Gateway_DeliveryLag, que el panel usa para mostrar el retraso de cada
    destino. Va aquí, antes del procedimiento, porque el vigilante la necesita.

    Cómo avisa: devuelve una fila por problema y, si hay alguno, lanza RAISERROR con severidad 16.
    Eso hace que el job de SQL Agent **falle**, y un job que falla sale en su historial y puede
    notificar a un operador. Se hace así a propósito, en vez de con sp_send_dbmail: no depende de
    que Database Mail esté configurado en esta instancia, que es compartida y no es nuestra.

    Conectar el aviso a un correo es el paso del DBA: definir un operador y marcar "notificar al
    fallar" en el job. Ver 14-sql-agent-jobs.sql.
*/

SET NOCOUNT ON;
GO

/*
    Retraso de cada destino: cuánto lleva esperando la entrega vencida más antigua que aún no se ha
    enviado. Solo salen los destinos que tienen alguna.

    Es MIN(NextAttemptAt) por destino, pero escrito así por una razón medida: el índice está partido
    por mes, y un MIN o un TOP (1) ORDER BY sobre todas las particiones obliga al motor a leer todo
    lo pendiente del destino —con 300.000 pendientes, 1.233 páginas—. Pedido partición por partición,
    cada una es una búsqueda que para en la primera fila: 80 páginas para el mismo resultado.
*/
CREATE OR ALTER FUNCTION dbo.fn_Gateway_DeliveryLag (@Now datetime2(3))
RETURNS TABLE
AS
RETURN
    SELECT
        e.Id                                    AS OutboundEndpointId,
        MIN(x.NextAttemptAt)                    AS OldestDueAt,
        DATEDIFF(SECOND, MIN(x.NextAttemptAt), @Now) AS LagSeconds
    FROM dbo.OutboundEndpoint AS e
    CROSS JOIN (
        SELECT p.partition_number
        FROM sys.partitions AS p
        WHERE p.object_id = OBJECT_ID(N'dbo.WebhookDelivery') AND p.index_id = 1
    ) AS pt
    CROSS APPLY (
        SELECT TOP (1) d.NextAttemptAt
        FROM dbo.WebhookDelivery AS d WITH (INDEX(IX_Delivery_DispatchByEndpoint))
        WHERE d.OutboundEndpointId = e.Id
          AND d.Status IN (0, 2)
          AND d.NextAttemptAt <= @Now
          AND d.ExpiresAt > @Now
          AND $PARTITION.PF_Monthly(d.CreatedAt) = pt.partition_number
        ORDER BY d.NextAttemptAt
    ) AS x
    GROUP BY e.Id;
GO

CREATE OR ALTER PROCEDURE dbo.sp_Gateway_Watchdog
    @MinMonthsOfPartitions int = 3,
    @MaxBacklog            int = 50000,
    @MaxLagMinutes         int = 15
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @problemas TABLE (Asunto varchar(60), Detalle nvarchar(400));

    /* ---------- 1. Margen de particiones ---------- */

    DECLARE @ultimaFrontera date = (
        SELECT MAX(CONVERT(date, CONVERT(datetime2(3), prv.value)))
        FROM sys.partition_functions pf
        JOIN sys.partition_range_values prv ON prv.function_id = pf.function_id
        WHERE pf.name = N'PF_Monthly');

    DECLARE @mesesDeMargen int = DATEDIFF(MONTH, CONVERT(date, SYSUTCDATETIME()), @ultimaFrontera);

    IF @ultimaFrontera IS NULL
    BEGIN
        INSERT INTO @problemas VALUES ('Particiones',
            N'No existe la función de partición PF_Monthly. El esquema no está completo.');
    END
    ELSE IF @mesesDeMargen < @MinMonthsOfPartitions
    BEGIN
        INSERT INTO @problemas VALUES ('Particiones',
            N'Quedan ' + CONVERT(nvarchar(10), @mesesDeMargen) + N' meses de particiones (última frontera '
            + CONVERT(nvarchar(10), @ultimaFrontera, 23) + N'). Cuando se agoten, la recepción empezará a '
            + N'devolver 503. Ejecutar sp_Gateway_EnsureFuturePartitions.');
    END

    /* ---------- 2. Backlog de entregas ---------- */

    /*
        Status IN (0, 2) es Pending y Retrying, lo que el despachador tiene por hacer. Se cuenta con
        el índice filtrado del claim, así que cuesta lo mismo con la tabla vacía que con quince
        millones de filas; NOLOCK porque es una cifra para vigilar, no para decidir nada.
    */
    DECLARE @backlog bigint = (
        SELECT COUNT_BIG(1)
        FROM dbo.WebhookDelivery WITH (NOLOCK)
        WHERE Status IN (0, 2));

    IF @backlog > @MaxBacklog
    BEGIN
        INSERT INTO @problemas VALUES ('Backlog',
            N'Hay ' + CONVERT(nvarchar(20), @backlog) + N' entregas pendientes, por encima del umbral de '
            + CONVERT(nvarchar(20), @MaxBacklog) + N'. Revisar si el despachador está en marcha y si algún '
            + N'destino está caído o limitando el ritmo.');
    END

    /* ---------- 3. Retraso de entrega ---------- */

    DECLARE @peorDestino nvarchar(200), @peorRetraso int;

    SELECT TOP (1) @peorDestino = o.Name, @peorRetraso = l.LagSeconds
    FROM dbo.fn_Gateway_DeliveryLag(SYSUTCDATETIME()) AS l
    JOIN dbo.OutboundEndpoint AS o ON o.Id = l.OutboundEndpointId
    ORDER BY l.LagSeconds DESC;

    IF @peorRetraso > @MaxLagMinutes * 60
    BEGIN
        INSERT INTO @problemas VALUES ('Retraso',
            N'El destino "' + @peorDestino + N'" tiene entregas esperando desde hace '
            + CONVERT(nvarchar(20), @peorRetraso / 60) + N' minutos, por encima del umbral de '
            + CONVERT(nvarchar(20), @MaxLagMinutes) + N'. O el destino no da abasto con su ritmo y '
            + N'concurrencia, o el despachador no está corriendo.');
    END

    /* ---------- 4. Leases huérfanos ---------- */

    /*
        Entregas reclamadas cuyo lease venció hace rato: el mantenimiento del despachador debería
        haberlas devuelto a la cola. Si se acumulan, es que el despachador no está corriendo.
    */
    DECLARE @huerfanas bigint = (
        SELECT COUNT_BIG(1)
        FROM dbo.WebhookDelivery WITH (NOLOCK)
        WHERE Status = 1 AND LeaseUntil < DATEADD(MINUTE, -15, SYSUTCDATETIME()));

    IF @huerfanas > 0
    BEGIN
        INSERT INTO @problemas VALUES ('Leases',
            N'Hay ' + CONVERT(nvarchar(20), @huerfanas) + N' entregas reclamadas con el lease vencido hace '
            + N'más de 15 minutos. El mantenimiento del despachador debería haberlas liberado: '
            + N'comprobar que el proceso está vivo.');
    END

    /* ---------- Resultado ---------- */

    SELECT
        Asunto,
        Detalle,
        ComprobadoUtc   = SYSUTCDATETIME(),
        MesesDeMargen   = @mesesDeMargen,
        Backlog         = @backlog,
        PeorRetrasoMin  = @peorRetraso / 60,
        LeasesHuerfanos = @huerfanas
    FROM @problemas;

    IF EXISTS (SELECT 1 FROM @problemas)
    BEGIN
        DECLARE @resumen nvarchar(2000) = N'';

        SELECT @resumen = @resumen + Asunto + N': ' + Detalle + NCHAR(10)
        FROM @problemas;

        -- Severidad 16: el job falla y queda visible en su historial.
        RAISERROR(N'Vigilante del gateway: %s', 16, 1, @resumen);
    END
END
GO
