$ErrorActionPreference = 'Stop'
# Foto de lo que está haciendo y esperando LocalDB durante la carga. Solo lectura.
$c = New-Object System.Data.SqlClient.SqlConnection 'Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=True'
$c.Open()
function Q($sql) { $cmd = $c.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 60; $t = New-Object System.Data.DataTable; $r = $cmd.ExecuteReader(); $t.Load($r); $r.Close(); return ,$t }

"--- peticiones activas del gateway, por espera y por consulta"
(Q @"
SELECT TOP (12) COUNT(*) AS n, r.wait_type, MAX(r.wait_time) AS max_wait_ms,
       LEFT(REPLACE(REPLACE(SUBSTRING(t.text, 1, 90), CHAR(13), ' '), CHAR(10), ' '), 90) AS consulta
FROM sys.dm_exec_requests r
JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE s.program_name = N'WebhookGateway'
GROUP BY r.wait_type, LEFT(REPLACE(REPLACE(SUBSTRING(t.text, 1, 90), CHAR(13), ' '), CHAR(10), ' '), 90)
ORDER BY n DESC
"@).Rows | ForEach-Object { "{0,4} {1,-22} {2,7} ms  {3}" -f $_.n, $_.wait_type, $_.max_wait_ms, $_.consulta }

"--- bloqueos (quién espera a quién)"
(Q @"
SELECT TOP (8) r.session_id, r.blocking_session_id, r.wait_type, r.wait_resource,
       LEFT(REPLACE(SUBSTRING(t.text, 1, 70), CHAR(10), ' '), 70) AS consulta
FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE r.blocking_session_id <> 0
"@).Rows | ForEach-Object { "{0} bloqueada por {1} [{2} {3}] {4}" -f $_.session_id, $_.blocking_session_id, $_.wait_type, $_.wait_resource, $_.consulta }

"--- sesiones del gateway abiertas"
(Q "SELECT COUNT(*) AS n FROM sys.dm_exec_sessions WHERE program_name = N'WebhookGateway'").Rows[0].n

"--- cola en WebhookGateway_dev"
(Q "SELECT Status, COUNT_BIG(*) AS n FROM WebhookGateway_dev.dbo.WebhookDelivery WITH (NOLOCK) GROUP BY Status ORDER BY Status").Rows | ForEach-Object { "estado {0}: {1}" -f $_.Status, $_.n }

"--- escalados de bloqueo en WebhookDelivery (acumulado)"
(Q "SELECT SUM(index_lock_promotion_count) AS esc, SUM(row_lock_wait_count) AS esperas_fila, SUM(row_lock_wait_in_ms) AS ms_esperando FROM sys.dm_db_index_operational_stats(DB_ID('WebhookGateway_dev'), OBJECT_ID('WebhookGateway_dev.dbo.WebhookDelivery'), NULL, NULL)").Rows | ForEach-Object { "escalados={0} esperas_de_fila={1} ms_esperando={2}" -f $_.esc, $_.esperas_fila, $_.ms_esperando }
$c.Close()
