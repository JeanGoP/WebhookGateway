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
