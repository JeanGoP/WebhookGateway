<#
    Prepara la base de DESARROLLO WebhookGateway_dev en el mismo servidor que producción.

    - Toma servidor, usuario y contraseña de src/WebhookGateway.Api/appsettings.json,
      pero SIEMPRE trabaja contra WebhookGateway_dev. El nombre de la base está fijo
      en este script y no se puede cambiar por parámetro: así nunca toca producción.
    - No ejecuta 00-database.sql (tiene "WebhookGateway" escrito a mano y cambiaría
      producción). Aplica sus ajustes aquí mismo, contra la base de desarrollo.
    - Tampoco ejecuta 05 (Resource Governor) ni 90 (datos de demostración).

    Uso (desde backend\db):
        .\setup-dev-db.ps1
#>

$ErrorActionPreference = 'Stop'
$Database = 'WebhookGateway_dev'

if ($Database -notlike '*_dev') { throw 'Protección: este script solo trabaja contra una base *_dev.' }

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw 'No se encuentra sqlcmd. Instálalo con: winget install Microsoft.SqlCmd'
}

# --- Credenciales desde appsettings.json (no se modifica el archivo) ---
$settingsPath = Join-Path $PSScriptRoot '..\src\WebhookGateway.Api\appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$builder = New-Object System.Data.Common.DbConnectionStringBuilder
$builder.set_ConnectionString($settings.Gateway.Sql.ConnectionString)

$server = $builder['Server']
$user = $builder['User Id']
$password = $builder['Password']

Write-Host "Servidor: $server" -ForegroundColor Cyan
Write-Host "Base:     $Database  (producción NO se toca)" -ForegroundColor Cyan
Write-Host ''

function Invoke-Sql([string[]] $extra) {
    & sqlcmd -S $server -U $user -P $password -C -b -I @extra
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd falló (código $LASTEXITCODE)." }
}

# --- Comprobar que la base de desarrollo existe ---
$exists = & sqlcmd -S $server -U $user -P $password -C -h -1 -W -Q "SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'$Database') IS NULL THEN 0 ELSE 1 END"
if (($exists | Select-Object -First 1).Trim() -ne '1') { throw "No existe la base $Database. Créala primero." }

# --- Paso 1: ajustes de la base (equivale a 00-database.sql, pero sobre _dev) ---
Write-Host '→ Ajustes de la base (RCSI, SIMPLE)' -ForegroundColor Cyan
Invoke-Sql @('-d', 'master', '-Q', @"
ALTER DATABASE [$Database] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
ALTER DATABASE [$Database] SET RECOVERY SIMPLE;
ALTER DATABASE [$Database] SET AUTO_SHRINK OFF;
ALTER DATABASE [$Database] SET AUTO_CLOSE OFF;
"@)

# --- Paso 2: esquema ---
$scripts = @(
    '01-schema.sql'
    '02-traffic-tables.sql'
    '03-users-audit.sql'
    '04-partition-maintenance.sql'
    '06-delivery-by-id.sql'
    '07-notifications.sql'
    '08-endpoint-health.sql'
    '09-subscribers.sql'
    '10-traffic-write-inbound.sql'
)

foreach ($script in $scripts) {
    Write-Host "→ $script" -ForegroundColor Cyan
    Invoke-Sql @('-d', $Database, '-i', (Join-Path $PSScriptRoot $script))
}

# --- Comprobación ---
Write-Host ''
Write-Host 'Esquema aplicado en la base de desarrollo. Comprobación:' -ForegroundColor Green
Invoke-Sql @('-d', $Database, '-Q', @"
SET NOCOUNT ON;
SELECT [Base] = DB_NAME();
SELECT [Tablas] = COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0;
SELECT [Procedimientos] = COUNT(*) FROM sys.procedures WHERE is_ms_shipped = 0;
SELECT [RCSI] = is_read_committed_snapshot_on, [Recuperacion] = recovery_model_desc
FROM sys.databases WHERE name = DB_NAME();
"@)
