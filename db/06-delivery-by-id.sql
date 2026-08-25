/*
    WebhookGateway — índice de búsqueda de una entrega por su id. Idempotente.

    Va en un script propio porque 02-traffic-tables.sql crea sus índices dentro del
    bloque que solo se ejecuta cuando la tabla no existe: añadirlo allí no llegaría
    nunca a una base de datos ya creada.

    Motivo: WebhookDelivery se agrupa por (CreatedAt, Id), como exige el particionado.
    Buscar por Id solo —lo que hace el reenvío manual del panel— recorrería la tabla
    entera. Es el mismo problema que UX_WebhookMessage_Id resuelve para los mensajes,
    y se resuelve igual.
*/

SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.WebhookDelivery') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'UX_Delivery_Id'
                     AND object_id = OBJECT_ID(N'dbo.WebhookDelivery'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_Delivery_Id
        ON dbo.WebhookDelivery (Id, CreatedAt)
        INCLUDE (MessageId, OutboundEndpointId, Status)
        WITH (DATA_COMPRESSION = PAGE) ON PS_Monthly(CreatedAt);
END
GO
