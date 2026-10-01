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
        WITH (DATA_COMPRESSION = PAGE)
        ON PS_Monthly(CreatedAt);
END
GO
