/*
    WebhookGateway — datos de prueba. Idempotente.

    NO es parte del esquema: es solo para poder probar de punta a punta antes de que
    exista el panel. Crea una integración con un endpoint de entrada sin autenticación y
    dos destinos, para ver el fanout funcionando.

    Cámbiale las URLs de destino por dos tuyas antes de ejecutarlo. Un sitio cómodo para
    obtener URLs que registran lo que reciben es https://webhook.site (crea dos).

    Para borrarlo todo después, al final del archivo hay un bloque comentado.
*/

SET NOCOUNT ON;
GO

DECLARE @DestinoA nvarchar(2000) = N'https://webhook.site/CAMBIA-ESTO-A';
DECLARE @DestinoB nvarchar(2000) = N'https://webhook.site/CAMBIA-ESTO-B';

/* ---------- Integración ---------- */

IF NOT EXISTS (SELECT 1 FROM dbo.Integration WHERE Slug = 'demo')
    INSERT INTO dbo.Integration (Name, Slug, Description)
    VALUES (N'Demo', 'demo', N'Integración de prueba creada por 90-seed-demo.sql');

DECLARE @IntegrationId int = (SELECT Id FROM dbo.Integration WHERE Slug = 'demo');

/* ---------- Endpoint de entrada ----------
   Sin autenticación y deduplicando por hash del cuerpo: así, si mandas el mismo JSON dos
   veces, la segunda se marca como duplicada y no genera entregas nuevas.               */

IF NOT EXISTS (SELECT 1 FROM dbo.InboundEndpoint WHERE IntegrationId = @IntegrationId AND Slug = 'pedidos')
    INSERT INTO dbo.InboundEndpoint (IntegrationId, Name, Slug, AuthType, DedupeStrategy)
    VALUES (@IntegrationId, N'Pedidos', 'pedidos', 0, 3);   -- AuthType None, Dedupe BodyHash

DECLARE @InboundId int =
    (SELECT Id FROM dbo.InboundEndpoint WHERE IntegrationId = @IntegrationId AND Slug = 'pedidos');

/* ---------- Dos destinos con ritmos distintos ----------
   El segundo va deliberadamente lento (6 por minuto) para que se vea el suavizado: manda
   veinte mensajes de golpe y observa cómo al primero le llegan enseguida y al segundo le
   van goteando.                                                                        */

IF NOT EXISTS (SELECT 1 FROM dbo.OutboundEndpoint WHERE IntegrationId = @IntegrationId AND Name = N'Destino rápido')
    INSERT INTO dbo.OutboundEndpoint
        (IntegrationId, Name, TargetUrl, AuthType, RateLimitPerMinute, MaxConcurrency, TimeoutSeconds, DeliveryWindowHours)
    VALUES (@IntegrationId, N'Destino rápido', @DestinoA, 0, 600, 4, 30, 72);

IF NOT EXISTS (SELECT 1 FROM dbo.OutboundEndpoint WHERE IntegrationId = @IntegrationId AND Name = N'Destino lento')
    INSERT INTO dbo.OutboundEndpoint
        (IntegrationId, Name, TargetUrl, AuthType, RateLimitPerMinute, MaxConcurrency, TimeoutSeconds, DeliveryWindowHours)
    VALUES (@IntegrationId, N'Destino lento', @DestinoB, 0, 6, 1, 30, 72);

/* ---------- Suscripciones: el fanout ---------- */

INSERT INTO dbo.Subscription (InboundEndpointId, OutboundEndpointId)
SELECT @InboundId, o.Id
FROM dbo.OutboundEndpoint AS o
WHERE o.IntegrationId = @IntegrationId
  AND NOT EXISTS (
      SELECT 1 FROM dbo.Subscription AS s
      WHERE s.InboundEndpointId = @InboundId AND s.OutboundEndpointId = o.Id);
GO

/* ---------- Qué quedó montado ---------- */

SELECT
    N'POST /in/' + i.Slug + N'/' + e.Slug AS [URL de recepción],
    o.Name                                AS [Destino],
    o.TargetUrl                           AS [URL destino],
    o.RateLimitPerMinute                  AS [Por minuto],
    CASE WHEN s.IsActive = 1 THEN N'sí' ELSE N'no' END AS [Activa]
FROM dbo.Subscription       AS s
JOIN dbo.InboundEndpoint    AS e ON e.Id = s.InboundEndpointId
JOIN dbo.Integration        AS i ON i.Id = e.IntegrationId
JOIN dbo.OutboundEndpoint   AS o ON o.Id = s.OutboundEndpointId
WHERE i.Slug = 'demo';
GO

/*
    Para ver qué está pasando mientras pruebas:

        SELECT TOP 20 Id, ReceivedAt, Status, BodySizeBytes
        FROM dbo.WebhookMessage ORDER BY ReceivedAt DESC;

        SELECT TOP 40 d.Id, d.MessageId, o.Name AS Destino, d.Status, d.AttemptCount,
               d.NextAttemptAt, d.LastStatusCode, d.LastError
        FROM dbo.WebhookDelivery AS d
        JOIN dbo.OutboundEndpoint AS o ON o.Id = d.OutboundEndpointId
        ORDER BY d.CreatedAt DESC, d.Id DESC;

        SELECT TOP 40 * FROM dbo.DeliveryAttempt ORDER BY StartedAt DESC;

    Los estados de entrega: 0 pendiente, 1 en vuelo, 2 reintentando, 3 entregada,
    4 fallida, 5 caducada, 6 cancelada.

    Para deshacer el seed (borra también su tráfico):

        DELETE a FROM dbo.DeliveryAttempt a
        JOIN dbo.WebhookDelivery d ON d.Id = a.DeliveryId
        JOIN dbo.WebhookMessage m ON m.Id = d.MessageId
        JOIN dbo.InboundEndpoint e ON e.Id = m.InboundEndpointId
        JOIN dbo.Integration i ON i.Id = e.IntegrationId WHERE i.Slug = 'demo';

        DELETE d FROM dbo.WebhookDelivery d
        JOIN dbo.WebhookMessage m ON m.Id = d.MessageId
        JOIN dbo.InboundEndpoint e ON e.Id = m.InboundEndpointId
        JOIN dbo.Integration i ON i.Id = e.IntegrationId WHERE i.Slug = 'demo';

        DELETE p FROM dbo.WebhookPayload p
        JOIN dbo.WebhookMessage m ON m.Id = p.MessageId AND m.ReceivedAt = p.ReceivedAt
        JOIN dbo.InboundEndpoint e ON e.Id = m.InboundEndpointId
        JOIN dbo.Integration i ON i.Id = e.IntegrationId WHERE i.Slug = 'demo';

        DELETE m FROM dbo.WebhookMessage m
        JOIN dbo.InboundEndpoint e ON e.Id = m.InboundEndpointId
        JOIN dbo.Integration i ON i.Id = e.IntegrationId WHERE i.Slug = 'demo';

        DELETE dd FROM dbo.MessageDedupe dd
        JOIN dbo.InboundEndpoint e ON e.Id = dd.InboundEndpointId
        JOIN dbo.Integration i ON i.Id = e.IntegrationId WHERE i.Slug = 'demo';

        DELETE s FROM dbo.Subscription s
        JOIN dbo.InboundEndpoint e ON e.Id = s.InboundEndpointId
        JOIN dbo.Integration i ON i.Id = e.IntegrationId WHERE i.Slug = 'demo';

        DELETE e FROM dbo.InboundEndpoint e
        JOIN dbo.Integration i ON i.Id = e.IntegrationId WHERE i.Slug = 'demo';

        DELETE o FROM dbo.OutboundEndpoint o
        JOIN dbo.Integration i ON i.Id = o.IntegrationId WHERE i.Slug = 'demo';

        DELETE FROM dbo.Integration WHERE Slug = 'demo';
*/
