$ErrorActionPreference = 'Continue'
# Una línea cada 30 s con lo que importa: recepción, cola, entregas y retraso. Solo lectura.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = Join-Path $here 'curva.log'
"hora | recibidos | rec/s | p95 rec | pendientes | en vuelo | reint | entregadas | destino rec | dest/s | dup | 503 | retraso s | modo" | Out-File $log -Encoding utf8
$c = New-Object System.Data.SqlClient.SqlConnection 'Server=(localdb)\MSSQLLocalDB;Database=WebhookGateway_dev;Integrated Security=True'
$c.Open()
$vacios = 0
for ($i = 0; $i -lt 480; $i++) {
    try {
        $g = Get-Content (Join-Path $here 'progreso-generador.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
        $d = Invoke-RestMethod http://127.0.0.1:5999/_stats -TimeoutSec 5
        $cmd = $c.CreateCommand(); $cmd.CommandTimeout = 60
        $cmd.CommandText = @"
SELECT
  SUM(CASE WHEN Status = 0 THEN 1 ELSE 0 END), SUM(CASE WHEN Status = 1 THEN 1 ELSE 0 END),
  SUM(CASE WHEN Status = 2 THEN 1 ELSE 0 END), SUM(CASE WHEN Status = 3 THEN 1 ELSE 0 END),
  (SELECT MAX(LagSeconds) FROM dbo.fn_Gateway_DeliveryLag(SYSUTCDATETIME()))
FROM dbo.WebhookDelivery WITH (NOLOCK);
"@
        $r = $cmd.ExecuteReader(); [void]$r.Read()
        $vals = 0..4 | ForEach-Object { if ($r.IsDBNull($_)) { 0 } else { $r[$_] } }
        $r.Close()
        $line = "{0:HH:mm:ss} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13}" -f (Get-Date), `
            $g.respondidas, $g.ritmo_por_s, $g.p95_ms, $vals[0], $vals[1], $vals[2], $vals[3], `
            $d.recibidas, $d.ritmo_ultimos_10s, $d.duplicadas, $d.rechazadas_503, $vals[4], $d.modo
        $line | Out-File $log -Append -Encoding utf8
        # Fin: el generador acabó y la cola quedó vacía dos veces seguidas.
        $activa = [long]$vals[0] + [long]$vals[1] + [long]$vals[2]
        if ($g.respondidas -ge $g.total -and $activa -eq 0) { $vacios++ } else { $vacios = 0 }
        if ($vacios -ge 2) { "FIN: cola vacía" | Out-File $log -Append -Encoding utf8; break }
    } catch {
        ("{0:HH:mm:ss} | error vigilando: {1}" -f (Get-Date), $_.Exception.Message) | Out-File $log -Append -Encoding utf8
    }
    Start-Sleep -Seconds 30
}
$c.Close()
