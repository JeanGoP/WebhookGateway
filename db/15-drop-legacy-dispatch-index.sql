/*
    WebhookGateway — borra el índice del claim antiguo. Idempotente.

    ⚠ ESTO SE EJECUTA **DESPUÉS** DE DESPLEGAR EL DESPACHADOR NUEVO, NO ANTES.

    IX_Delivery_Dispatch va por (NextAttemptAt, OutboundEndpointId) y servía al claim que reclamaba
    un lote global y lo repartía con ROW_NUMBER(). El despachador nuevo reclama por destino y usa
    IX_Delivery_DispatchByEndpoint, que lleva las mismas columnas en el otro orden.

    Mientras en producción siga corriendo el código anterior, el índice viejo es el que sostiene su
    claim: borrarlo antes del despliegue dejaría a la versión en producción recorriendo la tabla en
    cada ciclo. De ahí el guardia de abajo, que no es ceremonia: es lo único que separa "limpieza"
    de "degradar producción".

    Por qué borrarlo y no dejarlo ahí: los dos están filtrados por Status IN (0, 2), así que cada
    entrega pendiente mantiene una fila en cada uno. A 400.000 entregas al día eso es escritura y
    mantenimiento de índice que no sirve a ninguna consulta.
*/

SET NOCOUNT ON;
GO

/*
    Severidad 16 y SET NOEXEC ON, no severidad 20: los niveles 19 a 25 solo los puede usar un
    sysadmin, así que en un servidor compartido el guardia habría fallado con un error de permisos
    en vez de con este mensaje. NOEXEC hace que el resto del script no se ejecute.
*/
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Delivery_DispatchByEndpoint')
BEGIN
    RAISERROR(N'ABORTADO: no existe IX_Delivery_DispatchByEndpoint. Aplica primero db/11 y despliega el despachador nuevo; si no, el claim se queda sin indice.', 16, 1);
    SET NOEXEC ON;
END
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Delivery_Dispatch')
BEGIN
    DROP INDEX IX_Delivery_Dispatch ON dbo.WebhookDelivery;
    PRINT 'Borrado IX_Delivery_Dispatch.';
END
ELSE
BEGIN
    PRINT 'IX_Delivery_Dispatch ya no estaba.';
END
GO

/* Lo que queda sobre la tabla, para poder comprobarlo de un vistazo. */
SELECT Indice = i.name,
       Tipo = i.type_desc,
       Filtro = i.filter_definition,
       Claves = STUFF((SELECT ', ' + c.name
                       FROM sys.index_columns ic
                       JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                         AND ic.is_included_column = 0 AND ic.key_ordinal > 0
                       ORDER BY ic.key_ordinal
                       FOR XML PATH('')), 1, 2, '')
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID(N'dbo.WebhookDelivery') AND i.index_id > 0
ORDER BY i.index_id;
GO

SET NOEXEC OFF;
GO
