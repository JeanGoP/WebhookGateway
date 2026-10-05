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
