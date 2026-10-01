/*
    WebhookGateway — sistema de notificaciones y outbox de alertas. Idempotente.

    Separa la cola de salida transitoria (NotificationOutbox) del registro inmutable
    de auditoría de envíos (NotificationLog).

    Diseñado para drenado concurrente no bloqueante mediante UPDLOCK y READPAST.
*/

SET NOCOUNT ON;
GO

/* ------------------------------------------------------------------------
   1. NotificationOutbox — bandeja de salida transitoria de notificaciones
   ------------------------------------------------------------------------ */

IF OBJECT_ID(N'dbo.NotificationOutbox') IS NULL
BEGIN
    CREATE TABLE dbo.NotificationOutbox (
        Id                 bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_NotificationOutbox PRIMARY KEY CLUSTERED,
        CreatedAt          datetime2(3)  NOT NULL CONSTRAINT DF_NotificationOutbox_CreatedAt DEFAULT SYSUTCDATETIME(),
        ChannelType        tinyint       NOT NULL CONSTRAINT DF_NotificationOutbox_Channel DEFAULT 0, -- 0=Email, 1=Webhook
        Recipient          nvarchar(320) NOT NULL,
        IntegrationId      int           NULL,
        OutboundEndpointId int           NULL,
        DeliveryId         bigint        NULL,
        AlertType          tinyint       NOT NULL, -- 1=DeadLetter, 2=EndpointDegraded, 3=EndpointDown, 4=EndpointRecovered, 5=InboundError, 6=VerificationOptIn
        Subject            nvarchar(300) NOT NULL,
        MetadataJson       nvarchar(max) NOT NULL, -- Payload JSON con detalles del incidente (sin datos sensibles)
        Status             tinyint       NOT NULL CONSTRAINT DF_NotificationOutbox_Status DEFAULT 0, -- 0=Pending, 1=InFlight, 2=Retrying, 3=DeadLetter
        AttemptCount       smallint      NOT NULL CONSTRAINT DF_NotificationOutbox_Attempts DEFAULT 0,
        NextAttemptAt      datetime2(3)  NOT NULL CONSTRAINT DF_NotificationOutbox_NextAttempt DEFAULT SYSUTCDATETIME(),
        LeaseUntil         datetime2(3)  NULL,
        WorkerId           varchar(64)   NULL,
        LastError          nvarchar(1000) NULL
    );

    /*
        Índice filtrado del worker drenador: solo busca lo que está listo para enviar.
        Mantiene el costo de consulta mínimo sin importar cuántas filas pasen por la tabla.
    */
    CREATE NONCLUSTERED INDEX IX_NotificationOutbox_Drain
        ON dbo.NotificationOutbox (NextAttemptAt)
        INCLUDE (Id, ChannelType, Recipient, IntegrationId, OutboundEndpointId, DeliveryId, AlertType, Subject, MetadataJson, AttemptCount)
        WHERE Status IN (0, 2);

    /* Recuperación de leases huérfanos si el worker se recicla */
    CREATE NONCLUSTERED INDEX IX_NotificationOutbox_Lease
        ON dbo.NotificationOutbox (LeaseUntil)
        INCLUDE (Id)
        WHERE Status = 1;
END
GO

/* ------------------------------------------------------------------------
   2. NotificationLog — auditoría histórica inmutable de notificaciones
   ------------------------------------------------------------------------ */

IF OBJECT_ID(N'dbo.NotificationLog') IS NULL
BEGIN
    CREATE TABLE dbo.NotificationLog (
        Id                 bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_NotificationLog PRIMARY KEY CLUSTERED,
        SentAt             datetime2(3)  NOT NULL CONSTRAINT DF_NotificationLog_SentAt DEFAULT SYSUTCDATETIME(),
        ChannelType        tinyint       NOT NULL,
        Recipient          nvarchar(320) NOT NULL,
        IntegrationId      int           NULL,
        OutboundEndpointId int           NULL,
        DeliveryId         bigint        NULL,
        AlertType          tinyint       NOT NULL,
        Subject            nvarchar(300) NOT NULL,
        SmtpMessageId      nvarchar(255) NULL, -- Message-ID devuelto por Turbo SMTP (<...@turbo-smtp.com>)
        Status             tinyint       NOT NULL, -- 0=Delivered, 1=BouncedOrRejected, 2=FailedPermanent
        ErrorMessage       nvarchar(1000) NULL
    );

    /* Búsqueda rápida de historial de alertas recientes */
    CREATE NONCLUSTERED INDEX IX_NotificationLog_SentAt
        ON dbo.NotificationLog (SentAt DESC)
        INCLUDE (Recipient, IntegrationId, AlertType, Status);

    /* Historial filtrado por integración para el panel */
    CREATE NONCLUSTERED INDEX IX_NotificationLog_Integration
        ON dbo.NotificationLog (IntegrationId, SentAt DESC)
        WHERE IntegrationId IS NOT NULL;
END
GO
