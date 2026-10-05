$ErrorActionPreference = 'Stop'
# Verificación final de una prueba de carga. Solo lectura, solo LocalDB.
$c = New-Object System.Data.SqlClient.SqlConnection 'Server=(localdb)\MSSQLLocalDB;Database=WebhookGateway_dev;Integrated Security=True'
$c.Open()
$cmd = $c.CreateCommand(); $cmd.CommandTimeout = 120
$cmd.CommandText = @"
SELECT Status, COUNT_BIG(*) FROM dbo.WebhookDelivery GROUP BY Status ORDER BY Status;
SELECT COUNT(*) FROM (SELECT DeliveryId FROM dbo.DeliveryAttempt WHERE StatusCode BETWEEN 200 AND 299 GROUP BY DeliveryId HAVING COUNT(*) > 1) x;
SELECT SUM(index_lock_promotion_count), SUM(row_lock_wait_count) FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID('dbo.WebhookDelivery'), NULL, NULL);
SELECT HealthStatus FROM dbo.EndpointHealthState;
"@
$rd = $cmd.ExecuteReader()
while ($rd.Read()) { "entregas en estado {0}: {1}" -f $rd[0], $rd[1] }
[void]$rd.NextResult(); [void]$rd.Read(); "entregas con más de un 2xx: " + $rd[0]
[void]$rd.NextResult(); [void]$rd.Read(); "escalados: {0}  esperas de fila: {1}" -f $rd[0], $rd[1]
[void]$rd.NextResult(); if ($rd.Read()) { "salud final del destino (0 = sano): " + $rd[0] }
$rd.Close(); $c.Close()
