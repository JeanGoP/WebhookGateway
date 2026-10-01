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
