# =====================================================================
#  probar-webhook.ps1
#  Prueba de punta a punta contra un WebhookGateway desplegado.
#  Comprueba salud y dispara una ráfaga de webhooks al endpoint demo.
#
#  Requisito: haber aplicado db/90-seed-demo.sql (con URLs de webhook.site)
#  contra la base, para que exista /in/demo/pedidos y sus dos destinos.
#
#  Uso:
#    powershell -ExecutionPolicy Bypass -File .\probar-webhook.ps1 -BaseUrl https://webhookgateway-79i1.onrender.com
#    (opcional)  -Count 20
# =====================================================================

param(
    [string]$BaseUrl     = 'https://webhookgateway-79i1.onrender.com',
    [string]$Integration = 'demo',
    [string]$Endpoint    = 'pedidos',
    [int]$Count          = 20
)

# Windows PowerShell 5.1 negocia TLS 1.0/1.1 por defecto; Render exige TLS 1.2. Sin esto,
# cada conexión se corta con "La conexión ha terminado de forma inesperada".
[Net.ServicePointManager]::SecurityProtocol = `
    [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$BaseUrl   = $BaseUrl.TrimEnd('/')
$recepcion = "$BaseUrl/in/$Integration/$Endpoint"

function Get-Status([string]$url) {
    try {
        $r = Invoke-WebRequest -Uri $url -Method Get -UseBasicParsing -TimeoutSec 30
        return [int]$r.StatusCode
    } catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        return -1
    }
}

Write-Host ''
Write-Host "== Prueba de WebhookGateway ==" -ForegroundColor Cyan
Write-Host "Servicio: $BaseUrl"

# --- 1. Salud ---
Write-Host ''
Write-Host 'Salud:' -ForegroundColor Cyan
foreach ($p in '/', '/health/live', '/health/ready') {
    $code  = Get-Status "$BaseUrl$p"
    $color = if ($code -ge 200 -and $code -lt 400) { 'Green' } else { 'Red' }
    Write-Host ("  {0,-15} -> {1}" -f $p, $code) -ForegroundColor $color
}
Write-Host '  (/health/ready en 200 = la API alcanza el SQL)' -ForegroundColor DarkGray

# --- 2. Ráfaga de webhooks ---
# El endpoint demo deduplica por hash del cuerpo, así que cada mensaje lleva un nonce
# único: si no, el segundo idéntico se marcaría como duplicado y no generaría entregas.
Write-Host ''
Write-Host "Enviando $Count webhooks a $recepcion" -ForegroundColor Cyan
$ok = 0
for ($i = 1; $i -le $Count; $i++) {
    $body = @{
        pedido = $i
        nonce  = [guid]::NewGuid().ToString()
        ts     = (Get-Date).ToString('o')
    } | ConvertTo-Json -Compress

    try {
        $resp = Invoke-RestMethod -Uri $recepcion -Method Post -Body $body `
                    -ContentType 'application/json' -TimeoutSec 30
        Write-Host ("  #{0,-3} messageId={1}  status={2}  entregas={3}" -f `
                    $i, $resp.messageId, $resp.status, $resp.deliveries)
        $ok++
    } catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { '-' }
        Write-Host ("  #{0,-3} ERROR (HTTP {1}) {2}" -f $i, $code, $_.Exception.Message) -ForegroundColor Red
    }
}

Write-Host ''
Write-Host "Aceptados (202): $ok de $Count" -ForegroundColor Green
Write-Host ''
Write-Host 'Ahora mira tus URLs de webhook.site:' -ForegroundColor Cyan
Write-Host '  - El destino RÁPIDO (600/min) recibe casi todo enseguida.'
Write-Host '  - El destino LENTO (6/min) va goteando: ahí se ve el suavizado, que es'
Write-Host '    el valor del sistema (absorber un pico y drenarlo al ritmo del destino).'
Write-Host ''
Write-Host 'Si "entregas" viene 0, el seed no está aplicado o no hay suscripciones activas.'
Write-Host 'Si el envío da 404, falta aplicar db/90-seed-demo.sql contra la base.'
