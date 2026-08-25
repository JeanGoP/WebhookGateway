<#
    Aplica los scripts de esquema en orden contra una base ya creada.

    Los scripts son idempotentes: esto se puede volver a correr las veces que haga falta.
    El 05 (Resource Governor) queda fuera a propósito —es de producción, necesita permisos
    de instancia y lo aplica el DBA— y el 90 (datos de demostración) también, porque hay
    que editarle las URL antes.

    Autenticación de Windows:
        .\run-all.ps1 -Server MIPC\SQLEXPRESS

    Usuario y contraseña de SQL:
        .\run-all.ps1 -Server mi.servidor.com -User usuario -Password clave
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Server,
    [string] $Database = 'WebhookGateway',
    [string] $User,
    [string] $Password
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw 'No se encuentra sqlcmd. Instálalo con: winget install Microsoft.SqlCmd'
}

# -b hace que sqlcmd devuelva código de error si el script falla; sin eso los errores
# pasan desapercibidos y uno cree que aplicó un esquema que en realidad no aplicó.
$auth = if ($User) { @('-U', $User, '-P', $Password) } else { @('-E') }
$scripts = @(
    '00-database.sql'
    '01-schema.sql'
    '02-traffic-tables.sql'
    '03-users-audit.sql'
    '04-partition-maintenance.sql'
)

foreach ($script in $scripts) {
    $path = Join-Path $PSScriptRoot $script
    Write-Host "→ $script" -ForegroundColor Cyan

    & sqlcmd -S $Server -d $Database @auth -b -I -i $path

    if ($LASTEXITCODE -ne 0) {
        throw "Falló $script (código $LASTEXITCODE). Los anteriores sí se aplicaron; corrige y vuelve a lanzar."
    }
}

Write-Host ''
Write-Host 'Esquema aplicado. Comprobación:' -ForegroundColor Green

& sqlcmd -S $Server -d $Database @auth -b -Q @'
SELECT [Tablas] = COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0;
SELECT [Procedimientos] = COUNT(*) FROM sys.procedures WHERE is_ms_shipped = 0;
SELECT [Particiones] = COUNT(*) FROM sys.partition_range_values v
    JOIN sys.partition_functions f ON f.function_id = v.function_id;
SELECT [RCSI] = is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME();
'@
