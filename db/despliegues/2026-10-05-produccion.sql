/*
    DESPLIEGUE DE ESQUEMA EN PRODUCCIÓN — base WebhookGateway — preparado el 2026-10-05

    Aplica, en este orden, los scripts que le faltan a producción: 10, 11, 12, 13, 16, 17, 04 y 18.
    Todos son compatibles con el código que corre hoy (índice nuevo, columna que admite nulos, tabla
    nueva, procedimientos nuevos; el 10 mantiene parámetros y resultados), así que se aplican ANTES de
    publicar la versión nueva del gateway.

    NO incluye:
      - 15 (borra el índice viejo del claim): va DESPUÉS de publicar el código nuevo.
      - 14 (jobs de SQL Agent): se ejecuta aparte; ver docs/jobs-sql-agent.md.

    Cómo ejecutarlo en SSMS. Es T-SQL normal: NO hace falta el modo SQLCMD.
      1. Conectarse a 200.7.96.218 y abrir este archivo.
      2. F5. Da igual qué base esté elegida en el desplegable: el script se pone solo en
         WebhookGateway.
      3. Al final sale una tabla de comprobación: las 9 filas tienen que decir OK.

    Idempotente: se puede ejecutar las veces que haga falta; lo que ya está aplicado queda igual.

    Si algo falla, se detiene solo. Después de cada pieza hay un punto de control: si la pieza no
    quedó aplicada, sale "ABORTADO en la pieza …" y no se ejecuta nada más (ni la tabla final). La
    causa es el primer error en rojo de más arriba. Lo más probable es que algo estuviera
    bloqueado más de 10 s (error 1222): en ese caso basta con volver a ejecutarlo.

    Antes: copia de seguridad. Hubo una completa el 2026-10-05 a las 14:05 (hora del servidor).
    Vuelta atrás: 2026-10-05-produccion-vuelta-atras.sql, en esta misma carpeta.
*/
GO
-- Si en Mensajes sale "Incorrect syntax near '...'" (o un carácter raro) en la Línea 1, es la marca de
-- codificación del archivo, que se coló como texto en el lote del comentario de arriba. No afecta:
-- por eso el USE va en su propio lote.
USE [WebhookGateway];
GO
IF DB_NAME() <> N'WebhookGateway'
BEGIN
    RAISERROR(N'ABORTADO: la base activa no es WebhookGateway.', 16, 1);
    SET NOEXEC ON;
END
GO
-- Si algo está bloqueado más de 10 s, fallar en vez de hacer esperar a producción detrás.
SET LOCK_TIMEOUT 10000;
GO
-- Hora de inicio: los puntos de control la usan para saber qué se modificó en ESTA ejecución.
DECLARE @inicio datetime = DATEADD(SECOND, -1, GETDATE());
EXEC sys.sp_set_session_context @key = N'despliegue_inicio', @value = @inicio;
GO

-- =====================================================================
-- 10-traffic-write-inbound.sql
-- =====================================================================
PRINT '-> 10-traffic-write-inbound.sql';
GO
/*
    WebhookGateway — Procedimiento de Ingesta Atómica de Tráfico.
    Idempotente. Inserta mensaje, payload, deduplicación y entregas en un solo viaje de red.
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.sp_Traffic_WriteInbound
    @InboundEndpointId    int,
    @ReceivedAt           datetime2(3),
    @SourceIp             varchar(45),
    @HttpMethod           varchar(10),
    @HeadersJson          nvarchar(max),
    @QueryString          nvarchar(2000),
    @BodySizeBytes        int,
    @BodyHash             binary(32),
    @DedupeKey            varchar(200),
    @DedupeExpiresAt      datetime2(3),
    @PayloadEncoding      tinyint,
    @PayloadSizeBytes     int,
    @PayloadBody          varbinary(max),
    @PayloadStorageRef    nvarchar(500)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @MessageId bigint;
    DECLARE @Status tinyint = 0; -- Received
    DECLARE @ExistingMessageId bigint = NULL;
    DECLARE @CreatedDeliveries TABLE (Id bigint);

    BEGIN TRANSACTION;

    /*
        1. Reservar la clave de deduplicación ANTES de tocar nada más.

        UPDLOCK, HOLDLOCK sobre la clave agrupada PK_MessageDedupe (InboundEndpointId, DedupeKey)
        toma un bloqueo de rango: si dos peticiones con la misma clave llegan a la vez, la segunda
        espera aquí, y cuando entra ya ve la fila de la primera y se marca como duplicada.

        No se usa TRY/CATCH para detectar el 2627/2601. Con SET XACT_ABORT ON la transacción queda
        inservible dentro del CATCH (error 3930), así que el duplicado simultáneo acababa en un 503
        en vez de en una respuesta idempotente. Aquí el bloqueo hace que la violación de clave no
        pueda ocurrir, en vez de recogerla después.

        El bloqueo se toma primero, antes de cualquier escritura, para que todas las transacciones
        pidan los recursos en el mismo orden y no haya inversión que acabe en deadlock.
    */
    IF @DedupeKey IS NOT NULL
    BEGIN
        SELECT @ExistingMessageId = MessageId
        FROM dbo.MessageDedupe WITH (UPDLOCK, HOLDLOCK)
        WHERE InboundEndpointId = @InboundEndpointId AND DedupeKey = @DedupeKey;

        IF @ExistingMessageId IS NOT NULL
        BEGIN
            SET @Status = 1; -- Duplicate
        END
    END

    -- 2. Insertar el mensaje. El duplicado también se registra: saber que llegó dos veces es parte
    --    de lo que se consulta al depurar.
    INSERT INTO dbo.WebhookMessage
        (ReceivedAt, InboundEndpointId, SourceIp, HttpMethod, HeadersJson, QueryString, BodySizeBytes, BodyHash, Status)
    VALUES
        (@ReceivedAt, @InboundEndpointId, @SourceIp, @HttpMethod, @HeadersJson, @QueryString, @BodySizeBytes, @BodyHash, @Status);

    SET @MessageId = SCOPE_IDENTITY();

    -- 3. Insertar el payload
    INSERT INTO dbo.WebhookPayload
        (MessageId, ReceivedAt, Encoding, SizeBytes, Body, StorageRef)
    VALUES
        (@MessageId, @ReceivedAt, @PayloadEncoding, @PayloadSizeBytes, @PayloadBody, @PayloadStorageRef);

    -- 4. Guardar la clave reservada en el paso 1. Con el rango bloqueado, nadie ha podido
    --    insertarla entre medias: el INSERT no necesita red de seguridad.
    IF @DedupeKey IS NOT NULL AND @Status <> 1
    BEGIN
        INSERT INTO dbo.MessageDedupe (InboundEndpointId, DedupeKey, MessageId, ExpiresAt)
        VALUES (@InboundEndpointId, @DedupeKey, @MessageId, @DedupeExpiresAt);
    END

    -- 5. Crear entregas solo si NO es duplicado
    IF @Status <> 1
    BEGIN
        INSERT INTO dbo.WebhookDelivery
            (CreatedAt, MessageId, OutboundEndpointId, Status, NextAttemptAt, ExpiresAt)
        OUTPUT INSERTED.Id INTO @CreatedDeliveries(Id)
        SELECT
            @ReceivedAt,
            @MessageId,
            s.OutboundEndpointId,
            0, -- Pending
            @ReceivedAt,
            DATEADD(HOUR, o.DeliveryWindowHours, @ReceivedAt)
        FROM dbo.Subscription s
        INNER JOIN dbo.OutboundEndpoint o ON o.Id = s.OutboundEndpointId
        WHERE s.InboundEndpointId = @InboundEndpointId
          AND s.IsActive = 1
          AND o.IsActive = 1;

        -- Si no hubo ninguna suscripción activa, marcar como NoSubscriptions (2)
        IF @@ROWCOUNT = 0
        BEGIN
            SET @Status = 2; -- NoSubscriptions
            UPDATE dbo.WebhookMessage
            SET Status = 2
            WHERE ReceivedAt = @ReceivedAt AND Id = @MessageId;
        END
    END

    COMMIT TRANSACTION;

    -- Resultado 1: Cabecera del resultado
    SELECT @MessageId AS MessageId, @Status AS Status, @ExistingMessageId AS ExistingMessageId;

    -- Resultado 2: Identificadores de entregas creadas
    SELECT Id FROM @CreatedDeliveries;
END
GO

-- Punto de control: si la pieza 10 no quedó aplicada, no se sigue.
IF NOT (
       EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.sp_Traffic_WriteInbound')
               AND modify_date >= CAST(SESSION_CONTEXT(N'despliegue_inicio') AS datetime)))
BEGIN
    RAISERROR(N'ABORTADO en la pieza 10 (sp_Traffic_WriteInbound): no quedó aplicada y no se ejecutó nada más. La causa es el primer error en rojo de más arriba. Volver a ejecutar el script entero es seguro.', 16, 1);
    SET NOEXEC ON;
END
GO

-- =====================================================================
-- 11-delivery-dispatch-by-endpoint.sql
-- =====================================================================
PRINT '-> 11-delivery-dispatch-by-endpoint.sql';
GO
/*
    WebhookGateway — índice del claim por destino. Idempotente.

    El despachador pasó de reclamar un lote global y repartirlo con ROW_NUMBER() a reclamar por
    destino, cada uno a su ritmo. Ese cambio necesita otro orden de claves.

    IX_Delivery_Dispatch va por (NextAttemptAt, OutboundEndpointId): servía al claim global, que
    arrancaba por fecha. Un claim de un solo destino con ese índice tiene que recorrer todo lo
    vencido y descartar lo que no es suyo, de modo que el destino con cinco entregas paga el
    backlog de los demás. Con (OutboundEndpointId, NextAttemptAt) es una búsqueda directa.

    El índice antiguo NO se borra aquí a propósito: mientras en producción siga corriendo el
    código anterior, es el que sostiene su claim. Se puede borrar después del despliegue de la
    fase 5, y está anotado como tarea en docs/plan-escala-400k.md.
*/

SET NOCOUNT ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Delivery_DispatchByEndpoint')
BEGIN
    /*
        Filtrado por Status IN (0, 2) —Pending y Retrying— para que el índice solo contenga lo
        que está pendiente: cuesta lo mismo con la tabla vacía que con quince millones de filas.

        Alineado con PS_Monthly, así que CreatedAt entra en el índice por ser la columna de
        partición. Eso es lo que permite que el claim devuelva la clave completa (CreatedAt, Id)
        sin ir a la tabla.
    */
    CREATE NONCLUSTERED INDEX IX_Delivery_DispatchByEndpoint
        ON dbo.WebhookDelivery (OutboundEndpointId, NextAttemptAt)
        INCLUDE (Id, MessageId, AttemptCount, ExpiresAt)
        WHERE Status IN (0, 2)
        WITH (DATA_COMPRESSION = PAGE, ONLINE = ON)  -- Enterprise: sin bloquear escrituras
        ON PS_Monthly(CreatedAt);
END
GO

-- Punto de control: si la pieza 11 no quedó aplicada, no se sigue.
IF NOT (
       EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.WebhookDelivery')
               AND name = N'IX_Delivery_DispatchByEndpoint'))
BEGIN
    RAISERROR(N'ABORTADO en la pieza 11 (índice IX_Delivery_DispatchByEndpoint): no quedó aplicada y no se ejecutó nada más. La causa es el primer error en rojo de más arriba. Volver a ejecutar el script entero es seguro.', 16, 1);
    SET NOEXEC ON;
END
GO

-- =====================================================================
-- 12-purge-unpartitioned.sql
-- =====================================================================
PRINT '-> 12-purge-unpartitioned.sql';
GO
/*
    WebhookGateway — purga de las tablas que no están particionadas. Idempotente.

    Las de tráfico se vacían con TRUNCATE de particiones enteras (04-partition-maintenance.sql).
    Estas no: son pequeñas, no están particionadas y hay que borrarlas fila a fila, así que el
    mecanismo es otro —DELETE por lotes con un descanso entre lotes— y vive aparte.

    Aquí estaban sin purgar NotificationLog y AuditLog, que crecen para siempre, y los muertos de
    NotificationOutbox (Status = 3), que el worker deja ahí cuando agota los intentos de envío. Las
    de MessageDedupe y RefreshToken se movieron desde sp_Gateway_PurgeExpiredPartitions, porque son
    exactamente este mismo mecanismo y no tenían nada que hacer dentro de la purga de particiones.

    Por qué por lotes y con descanso: un DELETE de un millón de filas de golpe escala a bloqueo de
    tabla y hace crecer el log de transacciones. En una instancia compartida, eso es la diferencia
    entre una purga invisible y una llamada del DBA.
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.sp_Gateway_PurgeUnpartitionedLogs
    @NotificationRetentionDays int = 90,
    @AuditRetentionDays        int = 365,  -- el registro de auditoría se guarda más: es rastro de quién tocó qué
    @RefreshTokenGraceDays     int = 30,
    @BatchSize                 int = 10000,
    @DryRun                    bit = 1     -- por defecto no borra: primero se mira qué haría
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @now datetime2(3) = SYSUTCDATETIME();
    DECLARE @plan TABLE (TableName sysname, DateColumn sysname, Cutoff datetime2(3), Rows bigint);

    /*
        La lista es fija y está escrita aquí: ni los nombres de tabla ni el predicado extra vienen
        de un parámetro, así que el SQL dinámico de abajo no tiene por dónde inyectarse. Las fechas
        sí son parámetros, y van como parámetros de sp_executesql, no concatenadas.
    */
    DECLARE @targets TABLE (
        Seq        int IDENTITY(1,1),
        Name       sysname,
        DateColumn sysname,
        Cutoff     datetime2(3),
        Extra      nvarchar(200) NULL);

    INSERT INTO @targets (Name, DateColumn, Cutoff, Extra) VALUES
        -- Claves de deduplicación: ya vencidas, no hay nada que deduplicar con ellas.
        (N'MessageDedupe',      N'ExpiresAt',  @now,                                                      NULL),
        -- Tokens de refresco caducados, con margen para poder investigar un uso raro.
        (N'RefreshToken',       N'ExpiresAt',  DATEADD(DAY, -@RefreshTokenGraceDays, @now),               NULL),
        -- Historial de alertas enviadas.
        (N'NotificationLog',    N'SentAt',     DATEADD(DAY, -@NotificationRetentionDays, @now),           NULL),
        -- Alertas que nunca se pudieron enviar. Solo las muertas: las vivas las drena el worker.
        (N'NotificationOutbox', N'CreatedAt',  DATEADD(DAY, -@NotificationRetentionDays, @now),           N'AND Status = 3'),
        -- Auditoría del panel.
        (N'AuditLog',           N'OccurredAt', DATEADD(DAY, -@AuditRetentionDays, @now),                  NULL);

    DECLARE @seq int, @name sysname, @col sysname, @cutoff datetime2(3), @extra nvarchar(200);
    DECLARE @sql nvarchar(max), @rows bigint;

    DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
        SELECT Seq, Name, DateColumn, Cutoff, Extra FROM @targets ORDER BY Seq;

    OPEN cur;
    FETCH NEXT FROM cur INTO @seq, @name, @col, @cutoff, @extra;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @rows = 0;

        IF OBJECT_ID(N'dbo.' + @name) IS NOT NULL
        BEGIN
            IF @DryRun = 1
            BEGIN
                -- En seco se cuenta, que es justo para lo que sirve la semana de @DryRun = 1.
                SET @sql = N'SELECT @out = COUNT_BIG(1) FROM dbo.' + QUOTENAME(@name) + N' WITH (NOLOCK)'
                         + N' WHERE ' + QUOTENAME(@col) + N' < @cutoff ' + ISNULL(@extra, N'') + N';';

                EXEC sp_executesql @sql,
                    N'@cutoff datetime2(3), @out bigint OUTPUT',
                    @cutoff = @cutoff, @out = @rows OUTPUT;
            END
            ELSE
            BEGIN
                /*
                    El bucle va dentro del SQL dinámico para que todo esto sea una sola llamada en
                    vez de una por lote. Devuelve lo que borró de verdad, no una estimación.
                */
                SET @sql = N'
DECLARE @deleted int = 1, @total bigint = 0;
WHILE @deleted > 0
BEGIN
    DELETE TOP (@batch) FROM dbo.' + QUOTENAME(@name) + N'
    WHERE ' + QUOTENAME(@col) + N' < @cutoff ' + ISNULL(@extra, N'') + N';

    SET @deleted = @@ROWCOUNT;
    SET @total += @deleted;

    IF @deleted > 0 WAITFOR DELAY ''00:00:00.050'';
END
SET @out = @total;';

                EXEC sp_executesql @sql,
                    N'@cutoff datetime2(3), @batch int, @out bigint OUTPUT',
                    @cutoff = @cutoff, @batch = @BatchSize, @out = @rows OUTPUT;
            END

            INSERT INTO @plan (TableName, DateColumn, Cutoff, Rows)
            VALUES (@name, @col, @cutoff, @rows);
        END

        FETCH NEXT FROM cur INTO @seq, @name, @col, @cutoff, @extra;
    END

    CLOSE cur;
    DEALLOCATE cur;

    SELECT TableName, DateColumn, Cutoff, Rows, WouldDelete = @DryRun
    FROM @plan
    ORDER BY TableName;
END
GO

-- Punto de control: si la pieza 12 no quedó aplicada, no se sigue.
IF NOT (
       EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.sp_Gateway_PurgeUnpartitionedLogs')
               AND modify_date >= CAST(SESSION_CONTEXT(N'despliegue_inicio') AS datetime)))
BEGIN
    RAISERROR(N'ABORTADO en la pieza 12 (sp_Gateway_PurgeUnpartitionedLogs): no quedó aplicada y no se ejecutó nada más. La causa es el primer error en rojo de más arriba. Volver a ejecutar el script entero es seguro.', 16, 1);
    SET NOEXEC ON;
END
GO

-- =====================================================================
-- 13-watchdog.sql
-- =====================================================================
PRINT '-> 13-watchdog.sql';
GO
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

-- Punto de control: si la pieza 13 no quedó aplicada, no se sigue.
IF NOT (
       EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.fn_Gateway_DeliveryLag')
               AND modify_date >= CAST(SESSION_CONTEXT(N'despliegue_inicio') AS datetime))
   AND EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.sp_Gateway_Watchdog')
               AND modify_date >= CAST(SESSION_CONTEXT(N'despliegue_inicio') AS datetime)))
BEGIN
    RAISERROR(N'ABORTADO en la pieza 13 (fn_Gateway_DeliveryLag y sp_Gateway_Watchdog): no quedó aplicada y no se ejecutó nada más. La causa es el primer error en rojo de más arriba. Volver a ejecutar el script entero es seguro.', 16, 1);
    SET NOEXEC ON;
END
GO

-- =====================================================================
-- 16-inbound-transient-status.sql
-- =====================================================================
PRINT '-> 16-inbound-transient-status.sql';
GO
/*
    WebhookGateway — código de rechazo por endpoint de entrada. Idempotente.

    Cuando el gateway no puede guardar un webhook —SQL caído o sin responder— lo rechaza para que
    el emisor lo reintente; nunca responde 2xx sin haber persistido. Qué código hace reintentar
    depende del emisor: casi todos reintentan un 503, pero las apps del Marketplace de GHL solo
    reintentan un 429 y un 503 lo dan por perdido. Por eso se elige por endpoint.

    Nulo significa usar el global de la configuración (Gateway:Reception:TransientFailureStatusCode,
    503 si no se define).

    Compatible con el código desplegado: es una columna nueva y nula que la versión anterior no
    lee. Tiene que aplicarse ANTES de publicar la versión que la usa, porque EF Core la incluye en
    sus consultas de InboundEndpoint y sin ella fallarían.
*/

SET NOCOUNT ON;
GO

IF COL_LENGTH(N'dbo.InboundEndpoint', N'TransientFailureStatusCode') IS NULL
BEGIN
    ALTER TABLE dbo.InboundEndpoint ADD TransientFailureStatusCode smallint NULL;
END
GO

-- Punto de control: si la pieza 16 no quedó aplicada, no se sigue.
IF NOT (
       COL_LENGTH(N'dbo.InboundEndpoint', N'TransientFailureStatusCode') IS NOT NULL)
BEGIN
    RAISERROR(N'ABORTADO en la pieza 16 (columna TransientFailureStatusCode): no quedó aplicada y no se ejecutó nada más. La causa es el primer error en rojo de más arriba. Volver a ejecutar el script entero es seguro.', 16, 1);
    SET NOEXEC ON;
END
GO

-- =====================================================================
-- 17-retention-policy.sql
-- =====================================================================
PRINT '-> 17-retention-policy.sql';
GO
/*
    WebhookGateway — retención editable desde el panel. Idempotente.

    Una sola fila con los días que se guarda cada cosa. La purga nocturna
    (sp_Gateway_PurgeExpiredPartitions, en 04) la lee en cada ejecución, así que cambiarla en el panel
    surte efecto la noche siguiente sin tocar el job.

    Es global y no por integración, y no por gusto: la purga vacía particiones mensuales enteras con
    TRUNCATE, y una partición lleva mezcladas todas las integraciones de ese mes. Retener una
    integración más que otra exigiría borrar fila a fila, que es justo lo que la purga por
    particiones evita en un servidor compartido.

    Valores iniciales: mensajes y entregas 180 días, cuerpos 30, intentos 30.

    Compatible con el código desplegado: tabla nueva que la versión anterior no conoce. Aplicar
    antes de publicar la versión que la usa.
*/

SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.RetentionPolicy') IS NULL
BEGIN
    CREATE TABLE dbo.RetentionPolicy (
        Id           tinyint       NOT NULL CONSTRAINT PK_RetentionPolicy PRIMARY KEY
                                            CONSTRAINT CK_RetentionPolicy_Single CHECK (Id = 1),
        MetadataDays int           NOT NULL,   -- WebhookMessage y WebhookDelivery
        PayloadDays  int           NOT NULL,   -- WebhookPayload
        AttemptDays  int           NOT NULL,   -- DeliveryAttempt
        UpdatedAt    datetime2(3)  NOT NULL CONSTRAINT DF_RetentionPolicy_UpdatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedBy    nvarchar(200) NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.RetentionPolicy)
BEGIN
    INSERT INTO dbo.RetentionPolicy (Id, MetadataDays, PayloadDays, AttemptDays)
    VALUES (1, 180, 30, 30);
END
GO

-- Punto de control: si la pieza 17 no quedó aplicada, no se sigue.
IF NOT (
       EXISTS (SELECT 1 FROM sys.partitions WHERE object_id = OBJECT_ID(N'dbo.RetentionPolicy')
               AND index_id = 1 AND rows = 1))
BEGIN
    RAISERROR(N'ABORTADO en la pieza 17 (tabla RetentionPolicy con su fila): no quedó aplicada y no se ejecutó nada más. La causa es el primer error en rojo de más arriba. Volver a ejecutar el script entero es seguro.', 16, 1);
    SET NOEXEC ON;
END
GO

-- =====================================================================
-- 04-partition-maintenance.sql
-- =====================================================================
PRINT '-> 04-partition-maintenance.sql';
GO
/*
    WebhookGateway — mantenimiento de particiones. Idempotente.

    Dos procedimientos, pensados para correr desde un job nocturno:

      sp_Gateway_EnsureFuturePartitions   crea meses por delante
      sp_Gateway_PurgeExpiredPartitions   vacía los meses ya vencidos

    Aquí solo se purga lo particionado. Las tablas que no lo están tienen su propio procedimiento
    en 12-purge-unpartitioned.sql, porque se borran por lotes y no con TRUNCATE.

    La purga usa TRUNCATE TABLE ... WITH (PARTITIONS ...), disponible desde SQL Server
    2016. Es una operación mínimamente registrada y no necesita tablas de staging ni
    SWITCH, así que evita por completo el crecimiento del log que provocaría un DELETE
    masivo. En una instancia compartida esa diferencia es lo que separa una purga
    invisible de una llamada del DBA.
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.sp_Gateway_EnsureFuturePartitions
    @MonthsAhead int = 6
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @target date = DATEADD(MONTH, @MonthsAhead,
        DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1));

    DECLARE @last datetime2(3) = (
        SELECT MAX(CONVERT(datetime2(3), prv.value))
        FROM sys.partition_functions pf
        JOIN sys.partition_range_values prv ON prv.function_id = pf.function_id
        WHERE pf.name = N'PF_Monthly');

    DECLARE @next date = CONVERT(date, DATEADD(MONTH, 1, @last));

    WHILE @next <= @target
    BEGIN
        DECLARE @sql nvarchar(400) =
            N'ALTER PARTITION SCHEME PS_Monthly NEXT USED [PRIMARY];' +
            N'ALTER PARTITION FUNCTION PF_Monthly() SPLIT RANGE (''' +
            CONVERT(nvarchar(10), @next, 23) + N''');';
        EXEC sp_executesql @sql;

        SET @next = DATEADD(MONTH, 1, @next);
    END
END
GO

/*
    Los días salen de dbo.RetentionPolicy (17-retention-policy.sql), que se edita desde el panel. Los
    parámetros siguen existiendo para una ejecución a mano: si se pasan, mandan sobre la tabla. Sin
    tabla ni parámetros —el script 17 sin aplicar— se usan 180/30/30.
*/
CREATE OR ALTER PROCEDURE dbo.sp_Gateway_PurgeExpiredPartitions
    @MetadataRetentionDays int = NULL,
    @PayloadRetentionDays  int = NULL,
    @AttemptRetentionDays  int = NULL,
    @DryRun                bit = 1   -- por defecto no borra: primero se mira qué haría
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID(N'dbo.RetentionPolicy') IS NOT NULL
    BEGIN
        SELECT @MetadataRetentionDays = ISNULL(@MetadataRetentionDays, MetadataDays),
               @PayloadRetentionDays  = ISNULL(@PayloadRetentionDays, PayloadDays),
               @AttemptRetentionDays  = ISNULL(@AttemptRetentionDays, AttemptDays)
        FROM dbo.RetentionPolicy
        WHERE Id = 1;
    END

    SET @MetadataRetentionDays = ISNULL(@MetadataRetentionDays, 180);
    SET @PayloadRetentionDays  = ISNULL(@PayloadRetentionDays, 30);
    SET @AttemptRetentionDays  = ISNULL(@AttemptRetentionDays, 30);

    DECLARE @plan TABLE (
        TableName  sysname,
        Partitions varchar(200),
        Cutoff     date,
        Rows       bigint);

    /*
        Una partición se puede vaciar cuando su frontera SUPERIOR ya quedó por detrás
        del corte: solo entonces todas sus filas son más viejas que la retención.
    */
    DECLARE @tables TABLE (Name sysname, RetentionDays int);
    INSERT INTO @tables (Name, RetentionDays) VALUES
        (N'WebhookPayload',  @PayloadRetentionDays),
        (N'DeliveryAttempt', @AttemptRetentionDays),
        (N'WebhookDelivery', @MetadataRetentionDays),
        (N'WebhookMessage',  @MetadataRetentionDays);

    DECLARE @name sysname, @days int;
    DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT Name, RetentionDays FROM @tables;
    OPEN cur;
    FETCH NEXT FROM cur INTO @name, @days;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        DECLARE @cutoff date = CONVERT(date, DATEADD(DAY, -@days, SYSUTCDATETIME()));

        DECLARE @parts varchar(200) = NULL, @rows bigint = 0;

        SELECT
            @parts = STRING_AGG(CONVERT(varchar(10), p.partition_number), ','),
            @rows  = SUM(p.rows)
        FROM sys.partitions p
        JOIN sys.indexes i ON i.object_id = p.object_id AND i.index_id = p.index_id
        JOIN sys.partition_range_values prv
             ON prv.boundary_id = p.partition_number
        JOIN sys.partition_schemes ps ON ps.data_space_id = i.data_space_id
        JOIN sys.partition_functions pf
             ON pf.function_id = ps.function_id AND pf.function_id = prv.function_id
        WHERE p.object_id = OBJECT_ID(N'dbo.' + @name)
          AND i.index_id <= 1
          AND p.rows > 0
          AND CONVERT(date, CONVERT(datetime2(3), prv.value)) <= @cutoff;

        IF @parts IS NOT NULL
        BEGIN
            INSERT INTO @plan VALUES (@name, @parts, @cutoff, @rows);

            IF @DryRun = 0
            BEGIN
                DECLARE @truncate nvarchar(600) =
                    N'TRUNCATE TABLE dbo.' + QUOTENAME(@name) +
                    N' WITH (PARTITIONS (' + @parts + N'));';
                EXEC sp_executesql @truncate;
            END
        END

        FETCH NEXT FROM cur INTO @name, @days;
    END

    CLOSE cur;
    DEALLOCATE cur;

    /*
        Las tablas sin particionar —MessageDedupe, RefreshToken, NotificationLog, AuditLog y los
        muertos de NotificationOutbox— se purgan en sp_Gateway_PurgeUnpartitionedLogs
        (12-purge-unpartitioned.sql). Se borran fila a fila por lotes, que es otro mecanismo, y el
        job nocturno llama a los dos procedimientos.
    */
    SELECT TableName, Partitions, Cutoff, Rows, WouldDelete = @DryRun FROM @plan;
END
GO

-- Punto de control: si la pieza 04 no quedó aplicada, no se sigue.
IF NOT (
       EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.sp_Gateway_EnsureFuturePartitions')
               AND modify_date >= CAST(SESSION_CONTEXT(N'despliegue_inicio') AS datetime))
   AND EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.sp_Gateway_PurgeExpiredPartitions')
               AND modify_date >= CAST(SESSION_CONTEXT(N'despliegue_inicio') AS datetime)))
BEGIN
    RAISERROR(N'ABORTADO en la pieza 04 (sp_Gateway_EnsureFuturePartitions y sp_Gateway_PurgeExpiredPartitions): no quedó aplicada y no se ejecutó nada más. La causa es el primer error en rojo de más arriba. Volver a ejecutar el script entero es seguro.', 16, 1);
    SET NOEXEC ON;
END
GO

-- =====================================================================
-- 18-capacity-report.sql
-- =====================================================================
PRINT '-> 18-capacity-report.sql';
GO
/*
    WebhookGateway — informe de capacidad. Idempotente. Solo lee.

    Responde "¿se está quedando corta la configuración?" con lo que el gateway ya guarda: cada
    DeliveryAttempt apunta cuándo empezó y cuánto tardó la petición HTTP. De ahí sale, por la ley de
    Little, cuántas peticiones tuvo cada destino en vuelo de media (suma de duraciones ÷ duración de
    la ventana), y eso se compara con su MaxConcurrency. Sumado sobre todos los destinos, con el cupo
    global.

    Los topes de appsettings.json (MaxGlobalConcurrency, MaxEndpointsInParallel,
    MaxPerEndpointPerClaim, MaxPoolSize) SQL no los conoce: se pasan como parámetros, con los
    valores recomendados por defecto. Si en appsettings son otros, pasar los de allí.

    Cómo leerlo, por destino:
      - UsoConcurrencia cerca de 1 y retraso creciendo: el destino necesita más MaxConcurrency
        (si lo aguanta) o responder más rápido.
      - UsoRitmo cerca de 1: el límite es RateLimitPerMinute.
      - Retraso creciendo con UsoConcurrencia bajo: el freno está fuera del destino (cupo global,
        latencia a SQL, circuito abierto). Mirar el resumen global.
    Y global: UsoCupoGlobal cerca de 1 pide subir MaxGlobalConcurrency; DestinosConTrabajo por
    encima de MaxEndpointsInParallel significa destinos esperando turno; SesionesSql cerca de
    MaxPoolSize, riesgo de agotar el pool.

    PendientesVencidas y RetrasoSegundos son el estado de AHORA: solo salen cuando @Until es NULL. En
    una ventana pasada la cola ya no es la que era, y mostrarla engañaría.

    Coste: recorre los intentos de la ventana (por la clave agrupada, que empieza por StartedAt) y
    las entregas de las particiones posibles, y los cruza con un hash join: unas 600 páginas por
    cinco minutos a 70/s, medido en LocalDB. El percentil 95 es APPROX_PERCENTILE_CONT (SQL Server
    2022 o posterior): el exacto, como función de ventana, copiaba todas las filas a una tabla de
    trabajo y costaba 85.000 lecturas. Para decidir capacidad, un percentil aproximado basta.
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.sp_Gateway_CapacityReport
    @Minutes                int          = 15,
    @Until                  datetime2(3) = NULL,   -- fin de la ventana; NULL = ahora
    @AlertLagSeconds        int          = 120,    -- a partir de aquí un destino se considera acumulando
    @MaxGlobalConcurrency   int          = 256,
    @MaxEndpointsInParallel int          = 32,
    @MaxPerEndpointPerClaim int          = 40,
    @MaxPoolSize            int          = 120
AS
BEGIN
    SET NOCOUNT ON;
    -- Es una foto para vigilar: no debe esperar a nadie ni hacer esperar a nadie.
    SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

    DECLARE @hasta datetime2(3) = ISNULL(@Until, SYSUTCDATETIME());
    DECLARE @desde datetime2(3) = DATEADD(MINUTE, -@Minutes, @hasta);
    DECLARE @ventanaMs float = @Minutes * 60000.0;

    -- Una entrega intentada en la ventana no puede haberse creado antes de la ventana de entrega más
    -- larga: eso acota la búsqueda de cada entrega a una o dos particiones.
    DECLARE @entregasDesde datetime2(3) = DATEADD(HOUR,
        -(SELECT ISNULL(MAX(DeliveryWindowHours), 72) + 1 FROM dbo.OutboundEndpoint), @desde);

    DECLARE @porDestino TABLE (
        OutboundEndpointId int PRIMARY KEY, Intentos int, Exitos int,
        LatenciaMediaMs float, LatenciaP95Ms float, ConcurrenciaMedia float);

    INSERT INTO @porDestino
    SELECT d.OutboundEndpointId, COUNT(*),
           SUM(CASE WHEN a.StatusCode BETWEEN 200 AND 299 THEN 1 ELSE 0 END),
           AVG(CAST(a.DurationMs AS float)),
           APPROX_PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY a.DurationMs),
           SUM(CAST(a.DurationMs AS float)) / @ventanaMs
    FROM dbo.DeliveryAttempt AS a
    JOIN dbo.WebhookDelivery AS d WITH (INDEX(UX_Delivery_Id))
      ON d.Id = a.DeliveryId AND d.CreatedAt >= @entregasDesde AND d.CreatedAt <= @hasta
    WHERE a.StartedAt >= @desde AND a.StartedAt < @hasta
    GROUP BY d.OutboundEndpointId
    OPTION (HASH JOIN);

    DECLARE @pendientes TABLE (OutboundEndpointId int PRIMARY KEY, Vencidas bigint);
    DECLARE @retraso TABLE (OutboundEndpointId int PRIMARY KEY, LagSeconds int);

    IF @Until IS NULL
    BEGIN
        INSERT INTO @pendientes
        SELECT OutboundEndpointId, COUNT_BIG(*)
        FROM dbo.WebhookDelivery WITH (INDEX(IX_Delivery_DispatchByEndpoint))
        WHERE Status IN (0, 2) AND NextAttemptAt <= @hasta AND ExpiresAt > @hasta
        GROUP BY OutboundEndpointId;

        INSERT INTO @retraso SELECT OutboundEndpointId, LagSeconds FROM dbo.fn_Gateway_DeliveryLag(@hasta);
    END

    /* ---------- 1. Por destino ---------- */
    SELECT
        o.Id AS Destino, o.Name AS Nombre, o.IsActive AS Activo,
        o.MaxConcurrency, o.RateLimitPerMinute,
        ISNULL(p.Intentos, 0) / (1.0 * @Minutes)                         AS IntentosPorMinuto,
        CAST(p.LatenciaMediaMs AS int)                                    AS LatenciaMediaMs,
        CAST(p.LatenciaP95Ms AS int)                                      AS LatenciaP95Ms,
        CAST(p.ConcurrenciaMedia AS decimal(9, 1))                        AS ConcurrenciaMedia,
        CAST(p.ConcurrenciaMedia / NULLIF(o.MaxConcurrency, 0) AS decimal(5, 2)) AS UsoConcurrencia,
        CAST(ISNULL(p.Intentos, 0) / (1.0 * @Minutes)
             / NULLIF(o.RateLimitPerMinute, 0) AS decimal(5, 2))          AS UsoRitmo,
        CAST(1.0 - ISNULL(p.Exitos, 0) * 1.0 / NULLIF(p.Intentos, 0) AS decimal(5, 2)) AS TasaFallos,
        CASE WHEN @Until IS NULL THEN ISNULL(q.Vencidas, 0) END          AS PendientesVencidas,
        CASE WHEN @Until IS NULL THEN ISNULL(r.LagSeconds, 0) END         AS RetrasoSegundos,
        CASE
            WHEN p.Intentos IS NULL AND ISNULL(q.Vencidas, 0) = 0 THEN N'Sin tráfico en la ventana'
            WHEN 1.0 - ISNULL(p.Exitos, 0) * 1.0 / NULLIF(p.Intentos, 0) >= 0.20
                THEN N'El destino está fallando: no es capacidad, revisar sus errores'
            WHEN ISNULL(r.LagSeconds, 0) >= @AlertLagSeconds
                 AND p.ConcurrenciaMedia >= 0.85 * o.MaxConcurrency
                THEN N'Concurrencia al límite: subir MaxConcurrency si el destino lo aguanta, o que responda más rápido'
            WHEN ISNULL(r.LagSeconds, 0) >= @AlertLagSeconds AND o.RateLimitPerMinute > 0
                 AND ISNULL(p.Intentos, 0) / (1.0 * @Minutes) >= 0.9 * o.RateLimitPerMinute
                THEN N'Ritmo al límite: subir RateLimitPerMinute si el destino lo acepta'
            WHEN ISNULL(r.LagSeconds, 0) >= @AlertLagSeconds
                THEN N'Acumula sin llenar su concurrencia: el freno está fuera del destino (despachador parado, circuito abierto, cupo global o SQL)'
            WHEN o.MaxConcurrency / 4.0 > @MaxPerEndpointPerClaim
                THEN N'Al día, pero MaxPerEndpointPerClaim es menor que MaxConcurrency / 4: no llena su concurrencia'
            ELSE N'Al día'
        END AS Diagnostico
    FROM dbo.OutboundEndpoint AS o
    LEFT JOIN @porDestino AS p ON p.OutboundEndpointId = o.Id
    LEFT JOIN @pendientes AS q ON q.OutboundEndpointId = o.Id
    LEFT JOIN @retraso    AS r ON r.OutboundEndpointId = o.Id
    WHERE o.IsActive = 1 OR p.Intentos IS NOT NULL OR q.Vencidas IS NOT NULL
    ORDER BY ISNULL(r.LagSeconds, 0) DESC, p.ConcurrenciaMedia DESC;

    /* ---------- 2. Global ---------- */
    DECLARE @concTotal float = (SELECT ISNULL(SUM(ConcurrenciaMedia), 0) FROM @porDestino);
    DECLARE @conTrabajo int = CASE WHEN @Until IS NULL THEN (SELECT COUNT(*) FROM @retraso) END;
    DECLARE @sesiones int =
        CASE WHEN HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') = 1
             THEN (SELECT COUNT(*) FROM sys.dm_exec_sessions
                   WHERE program_name = N'WebhookGateway' AND database_id = DB_ID())
        END;

    SELECT
        @desde AS Desde, @hasta AS Hasta,
        CAST(@concTotal AS decimal(9, 1))                          AS ConcurrenciaTotal,
        @MaxGlobalConcurrency                                      AS MaxGlobalConcurrency,
        CAST(@concTotal / @MaxGlobalConcurrency AS decimal(5, 2))  AS UsoCupoGlobal,
        @conTrabajo                                                AS DestinosConTrabajo,  -- NULL en ventanas pasadas
        @MaxEndpointsInParallel                                    AS MaxEndpointsInParallel,
        @sesiones                                                  AS SesionesSql,   -- NULL sin VIEW SERVER STATE
        @MaxPoolSize                                               AS MaxPoolSize,
        CASE
            WHEN @concTotal >= 0.85 * @MaxGlobalConcurrency
                THEN N'Cupo global al límite: subir MaxGlobalConcurrency'
            WHEN @conTrabajo > @MaxEndpointsInParallel
                THEN N'Hay destinos esperando turno: subir MaxEndpointsInParallel (y vigilar el pool)'
            WHEN @sesiones >= 0.85 * @MaxPoolSize
                THEN N'Pool de SQL cerca del tope: subir MaxPoolSize o revisar quién lo ocupa'
            ELSE N'Holgado'
        END AS Diagnostico;
END
GO

-- Punto de control: si la pieza 18 no quedó aplicada, no se sigue.
IF NOT (
       EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.sp_Gateway_CapacityReport')
               AND modify_date >= CAST(SESSION_CONTEXT(N'despliegue_inicio') AS datetime)))
BEGIN
    RAISERROR(N'ABORTADO en la pieza 18 (sp_Gateway_CapacityReport): no quedó aplicada y no se ejecutó nada más. La causa es el primer error en rojo de más arriba. Volver a ejecutar el script entero es seguro.', 16, 1);
    SET NOEXEC ON;
END
GO

-- =====================================================================
-- Comprobación: todo tiene que decir OK.
-- =====================================================================
SELECT Pieza, CASE WHEN Ok = 1 THEN 'OK' ELSE 'FALTA' END AS Estado
FROM (VALUES
  ('10 ingesta con reserva de clave',  CASE WHEN EXISTS (SELECT 1 FROM sys.sql_modules WHERE object_id = OBJECT_ID('dbo.sp_Traffic_WriteInbound') AND definition LIKE '%UPDLOCK, HOLDLOCK%') THEN 1 ELSE 0 END),
  ('11 IX_Delivery_DispatchByEndpoint', CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Delivery_DispatchByEndpoint') THEN 1 ELSE 0 END),
  ('12 sp_Gateway_PurgeUnpartitionedLogs', CASE WHEN OBJECT_ID('dbo.sp_Gateway_PurgeUnpartitionedLogs') IS NOT NULL THEN 1 ELSE 0 END),
  ('13 fn_Gateway_DeliveryLag',        CASE WHEN OBJECT_ID('dbo.fn_Gateway_DeliveryLag') IS NOT NULL THEN 1 ELSE 0 END),
  ('13 vigilante con @MaxLagMinutes',  CASE WHEN EXISTS (SELECT 1 FROM sys.parameters WHERE object_id = OBJECT_ID('dbo.sp_Gateway_Watchdog') AND name = '@MaxLagMinutes') THEN 1 ELSE 0 END),
  ('16 TransientFailureStatusCode',    CASE WHEN COL_LENGTH('dbo.InboundEndpoint', 'TransientFailureStatusCode') IS NOT NULL THEN 1 ELSE 0 END),
  ('17 RetentionPolicy con su fila',   CASE WHEN OBJECT_ID('dbo.RetentionPolicy') IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.RetentionPolicy) THEN 1 ELSE 0 END),
  ('04 purga que lee la retención',    CASE WHEN EXISTS (SELECT 1 FROM sys.sql_modules WHERE object_id = OBJECT_ID('dbo.sp_Gateway_PurgeExpiredPartitions') AND definition LIKE '%RetentionPolicy%') THEN 1 ELSE 0 END),
  ('18 sp_Gateway_CapacityReport',     CASE WHEN OBJECT_ID('dbo.sp_Gateway_CapacityReport') IS NOT NULL THEN 1 ELSE 0 END)
) AS c(Pieza, Ok);
GO
SET NOEXEC OFF;
GO
