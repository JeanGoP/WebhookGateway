$ErrorActionPreference = 'Stop'
# Solo LocalDB. Nunca el servidor compartido.
$c = New-Object System.Data.SqlClient.SqlConnection 'Server=(localdb)\MSSQLLocalDB;Database=WebhookGateway_dev;Integrated Security=True'
$c.Open()
$cmd = $c.CreateCommand()
$cmd.CommandText = @"
IF DB_NAME() <> N'WebhookGateway_dev' OR @@SERVERNAME NOT LIKE N'%LOCALDB%'
    THROW 50000, N'Proteccion: esto solo corre contra WebhookGateway_dev en LocalDB.', 1;

-- Tráfico limpio: la prueba empieza en cero.
TRUNCATE TABLE dbo.WebhookDelivery;
TRUNCATE TABLE dbo.WebhookPayload;
TRUNCATE TABLE dbo.WebhookMessage;
TRUNCATE TABLE dbo.DeliveryAttempt;
DELETE FROM dbo.MessageDedupe;
DELETE FROM dbo.EndpointHealthState;

-- Los usuarios se conservan: el admin que creaste en la prueba anterior sigue valiendo.

-- Entrada como GHL: deduplicación por webhookId y 429 si SQL falla.
UPDATE dbo.InboundEndpoint
SET DedupeStrategy = 2, DedupeSource = 'webhookId', TransientFailureStatusCode = 429
WHERE Slug = 'leads';

-- El destino: el falso local, sin límite de ritmo, 96 a la vez, 10 s de timeout.
UPDATE dbo.OutboundEndpoint
SET TargetUrl = N'http://127.0.0.1:5999/hook', RateLimitPerMinute = 0, MaxConcurrency = 96,
    TimeoutSeconds = 10, Name = N'Destino falso (carga)'
WHERE Name LIKE N'Destino%';

SELECT o.Name, o.TargetUrl, o.RateLimitPerMinute, o.MaxConcurrency, o.TimeoutSeconds,
       (SELECT COUNT(*) FROM dbo.Subscription s WHERE s.OutboundEndpointId = o.Id) AS Suscripciones,
       (SELECT COUNT(*) FROM dbo.OutboundEndpoint) AS DestinosEnLaBase,
       (SELECT COUNT(*) FROM dbo.WebhookDelivery) AS Entregas,
       (SELECT COUNT(*) FROM dbo.AppUser) AS Usuarios
FROM dbo.OutboundEndpoint o;
"@
$r = $cmd.ExecuteReader()
while ($r.Read()) { for ($i = 0; $i -lt $r.FieldCount; $i++) { "{0} = {1}" -f $r.GetName($i), $r[$i] } }
$r.Close(); $c.Close()
