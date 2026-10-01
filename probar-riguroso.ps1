# =====================================================================
#  probar-riguroso.ps1
#  Prueba rigurosa de estrés concurrente contra WebhookGateway.
# =====================================================================

param(
    [string]$BaseUrl     = 'https://sintesiserp.com.co/API_WebhookGateway',
    [string]$Integration = 'demo',
    [string]$Endpoint    = 'pedidos',
    [int]$Count          = 100,
    [int]$BatchSize      = 20
)

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$BaseUrl = $BaseUrl.TrimEnd('/')
$recepcion = "$BaseUrl/in/$Integration/$Endpoint"

Write-Host ''
Write-Host "== Prueba Rigurosa de WebhookGateway ==" -ForegroundColor Cyan
Write-Host "Servicio: $BaseUrl"
Write-Host "Endpoint: $recepcion"
Write-Host "Cantidad: $Count webhooks concurrentes (Lotes de $BatchSize)"
Write-Host ''

# 1. Verificar salud
Write-Host '1. Verificando Salud...' -ForegroundColor Cyan
try {
    $health = Invoke-WebRequest -Uri "$BaseUrl/health/ready" -Method Get -UseBasicParsing -TimeoutSec 10
    Write-Host "  /health/ready -> $($health.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "  /health/ready -> ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit
}

# 2. Prueba concurrente usando HttpClient para mayor rendimiento
Write-Host ''
Write-Host '2. Disparando ráfaga concurrente...' -ForegroundColor Cyan

$code = @"
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

public class WebhookTester
{
    private static readonly HttpClient client = new HttpClient();

    public static async Task<string> SendRequestsAsync(string url, int count, int batchSize)
    {
        var tasks = new List<Task<HttpResponseMessage>>();
        int successCount = 0;
        int errorCount = 0;

        // Vamos a mandar un duplicado intencional a la mitad
        string duplicatedNonce = Guid.NewGuid().ToString();

        for (int i = 1; i <= count; i++)
        {
            string nonce = (i == count / 2 || i == (count / 2) + 1) ? duplicatedNonce : Guid.NewGuid().ToString();
            string json = string.Format("{{\"pedido\": {0}, \"nonce\": \"{1}\", \"ts\": \"{2:O}\"}}", i, nonce, DateTime.UtcNow);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            tasks.Add(client.PostAsync(url, content));

            if (tasks.Count >= batchSize || i == count)
            {
                var results = await Task.WhenAll(tasks);
                foreach (var r in results)
                {
                    if (r.IsSuccessStatusCode) 
                    {
                        successCount++;
                    }
                    else 
                    {
                        errorCount++;
                    }
                }
                tasks.Clear();
            }
        }

        return string.Format("Exitosos (202): {0} | Errores: {1} | Nota: 2 peticiones compartieron el mismo Nonce para probar deduplicación.", successCount, errorCount);
    }
}
"@

Add-Type -TypeDefinition $code -Language CSharp -ReferencedAssemblies "System.Net.Http"

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$result = [WebhookTester]::SendRequestsAsync($recepcion, $Count, $BatchSize).GetAwaiter().GetResult()
$stopwatch.Stop()

Write-Host "  Resultado: $result" -ForegroundColor Green
Write-Host "  Tiempo total de envío: $($stopwatch.ElapsedMilliseconds) ms" -ForegroundColor Yellow

# 3. Instrucciones
Write-Host ''
Write-Host '3. Ahora monitorea tus URLs de webhook.site:' -ForegroundColor Cyan
Write-Host '  - Destino RÁPIDO (9c322679...): Debió recibir ~100 peticiones casi al instante.'
Write-Host '  - Destino LENTO (13b755fb...): Recibirá 6 por minuto (1 cada 10 segundos).'
Write-Host '  - Comprueba que a pesar de enviar 100 peticiones en menos de un segundo, IIS no se cayó.'
Write-Host '  - También deberías notar que un payload fue deduplicado (el Dispatcher solo enviará 99 a cada destino en lugar de 100, aunque API aceptó 100).'
Write-Host ''
