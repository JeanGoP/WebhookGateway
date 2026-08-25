/*
    WebhookGateway — mantenimiento de particiones. Idempotente.

    Dos procedimientos, pensados para correr desde un job nocturno:

      sp_Gateway_EnsureFuturePartitions   crea meses por delante
      sp_Gateway_PurgeExpiredPartitions   vacía los meses ya vencidos

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

CREATE OR ALTER PROCEDURE dbo.sp_Gateway_PurgeExpiredPartitions
    @MetadataRetentionDays int = 365,
    @PayloadRetentionDays  int = 90,
    @AttemptRetentionDays  int = 90,
    @DryRun                bit = 1   -- por defecto no borra: primero se mira qué haría
AS
BEGIN
    SET NOCOUNT ON;

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

    /* Las claves de deduplicación son pocas y de vida corta: un DELETE normal basta. */
    IF @DryRun = 0
    BEGIN
        DELETE TOP (50000) FROM dbo.MessageDedupe WHERE ExpiresAt < SYSUTCDATETIME();
        DELETE TOP (50000) FROM dbo.RefreshToken  WHERE ExpiresAt < DATEADD(DAY, -30, SYSUTCDATETIME());
    END

    SELECT TableName, Partitions, Cutoff, Rows, WouldDelete = @DryRun FROM @plan;
END
GO
