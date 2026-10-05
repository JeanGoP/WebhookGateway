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
