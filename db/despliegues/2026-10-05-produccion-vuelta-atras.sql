/*
    VUELTA ATRÁS del despliegue de esquema en la base de PRODUCCIÓN WebhookGateway (2026-10-05).
    Deja la base exactamente como estaba antes de aplicar 10, 11, 12, 13, 16, 17, 04 y 18.
    Solo se ejecuta si hay que deshacer, y con el código anterior todavía publicado.
*/
GO
-- Si en Mensajes sale "Incorrect syntax near '...'" (o un carácter raro) en la Línea 1, es la marca de
-- codificación del archivo, que se coló como texto en el lote del comentario de arriba. No afecta:
-- por eso el USE va en su propio lote.
USE [WebhookGateway];
GO
IF DB_NAME() <> N'WebhookGateway' BEGIN RAISERROR(N'No es la base WebhookGateway.', 16, 1); SET NOEXEC ON; END
GO
-- Definición original de dbo.sp_Traffic_WriteInbound
DROP PROCEDURE IF EXISTS dbo.sp_Traffic_WriteInbound;
GO
CREATE   PROCEDURE dbo.sp_Traffic_WriteInbound
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

    -- 1. ComprobaciÃ³n rÃ¡pida de deduplicaciÃ³n
    IF @DedupeKey IS NOT NULL
    BEGIN
        SELECT @ExistingMessageId = MessageId 
        FROM dbo.MessageDedupe 
        WHERE InboundEndpointId = @InboundEndpointId AND DedupeKey = @DedupeKey;

        IF @ExistingMessageId IS NOT NULL
        BEGIN
            SET @Status = 1; -- Duplicate
        END
    END

    -- 2. Insertar el mensaje
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

    -- 4. DeduplicaciÃ³n optimista: si no era duplicado conocido, insertar clave
    IF @DedupeKey IS NOT NULL AND @Status <> 1
    BEGIN
        BEGIN TRY
            INSERT INTO dbo.MessageDedupe (InboundEndpointId, DedupeKey, MessageId, ExpiresAt)
            VALUES (@InboundEndpointId, @DedupeKey, @MessageId, @DedupeExpiresAt);
        END TRY
        BEGIN CATCH
            -- Si otra transacciÃ³n concurrente insertÃ³ la misma clave en este milisegundo (2627 / 2601)
            IF ERROR_NUMBER() IN (2627, 2601)
            BEGIN
                SELECT @ExistingMessageId = MessageId 
                FROM dbo.MessageDedupe 
                WHERE InboundEndpointId = @InboundEndpointId AND DedupeKey = @DedupeKey;

                SET @Status = 1; -- Duplicate

                UPDATE dbo.WebhookMessage 
                SET Status = 1 
                WHERE ReceivedAt = @ReceivedAt AND Id = @MessageId;
            END
            ELSE
            BEGIN
                THROW;
            END
        END CATCH
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

        -- Si no hubo ninguna suscripciÃ³n activa, marcar como NoSubscriptions (2)
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
-- Definición original de dbo.sp_Gateway_EnsureFuturePartitions
DROP PROCEDURE IF EXISTS dbo.sp_Gateway_EnsureFuturePartitions;
GO
CREATE   PROCEDURE dbo.sp_Gateway_EnsureFuturePartitions
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
-- Definición original de dbo.sp_Gateway_PurgeExpiredPartitions
DROP PROCEDURE IF EXISTS dbo.sp_Gateway_PurgeExpiredPartitions;
GO
CREATE   PROCEDURE dbo.sp_Gateway_PurgeExpiredPartitions
    @MetadataRetentionDays int = 365,
    @PayloadRetentionDays  int = 30,
    @AttemptRetentionDays  int = 90,
    @DryRun                bit = 1   -- por defecto no borra: primero se mira quÃ© harÃ­a
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @plan TABLE (
        TableName  sysname,
        Partitions varchar(200),
        Cutoff     date,
        Rows       bigint);

    /*
        Una particiÃ³n se puede vaciar cuando su frontera SUPERIOR ya quedÃ³ por detrÃ¡s
        del corte: solo entonces todas sus filas son mÃ¡s viejas que la retenciÃ³n.
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

    /* Purga por lotes con descanso para evitar contenciÃ³n de candados en rÃ¡fagas nocturnas */
    IF @DryRun = 0
    BEGIN
        DECLARE @deletedDedupe int = 1;
        WHILE @deletedDedupe > 0
        BEGIN
            DELETE TOP (10000) FROM dbo.MessageDedupe WHERE ExpiresAt < SYSUTCDATETIME();
            SET @deletedDedupe = @@ROWCOUNT;
            IF @deletedDedupe > 0
                WAITFOR DELAY '00:00:00.050';
        END;

        DECLARE @deletedRefresh int = 1;
        WHILE @deletedRefresh > 0
        BEGIN
            DELETE TOP (10000) FROM dbo.RefreshToken WHERE ExpiresAt < DATEADD(DAY, -30, SYSUTCDATETIME());
            SET @deletedRefresh = @@ROWCOUNT;
            IF @deletedRefresh > 0
                WAITFOR DELAY '00:00:00.050';
        END;
    END

    SELECT TableName, Partitions, Cutoff, Rows, WouldDelete = @DryRun FROM @plan;
END
GO
-- Lo que se creó nuevo, se quita.
DROP PROCEDURE IF EXISTS dbo.sp_Gateway_CapacityReport;
DROP PROCEDURE IF EXISTS dbo.sp_Gateway_Watchdog;
DROP FUNCTION  IF EXISTS dbo.fn_Gateway_DeliveryLag;
DROP PROCEDURE IF EXISTS dbo.sp_Gateway_PurgeUnpartitionedLogs;
DROP TABLE     IF EXISTS dbo.RetentionPolicy;
IF COL_LENGTH(N'dbo.InboundEndpoint', N'TransientFailureStatusCode') IS NOT NULL
    ALTER TABLE dbo.InboundEndpoint DROP COLUMN TransientFailureStatusCode;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Delivery_DispatchByEndpoint' AND object_id = OBJECT_ID(N'dbo.WebhookDelivery'))
    DROP INDEX IX_Delivery_DispatchByEndpoint ON dbo.WebhookDelivery;
GO
SET NOEXEC OFF;
GO
