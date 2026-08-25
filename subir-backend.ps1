# =====================================================================
#  subir-backend.ps1
#  Inicializa git y sube SOLO el backend de WebhookGateway a GitHub.
#  Con guardas: aborta si detecta un secreto o archivos de frontend/.
#  Uso:  powershell -ExecutionPolicy Bypass -File .\subir-backend.ps1
# =====================================================================

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # los exit-code de git no abortan el script
Set-Location -Path $PSScriptRoot

$RepoUrl = 'https://github.com/JeanGoP/WebhookGateway.git'

Write-Host ''
Write-Host '== WebhookGateway: subir el backend a GitHub ==' -ForegroundColor Cyan

# --- Identidad de git (solo si no está configurada) ---
if (-not (git config user.email)) {
    git config user.email 'JeanGoP@users.noreply.github.com'
    git config user.name  'JeanGoP'
    Write-Host 'Identidad de git local configurada (puedes cambiarla luego).'
}

# --- 1. Repositorio ---
if (-not (Test-Path .git)) {
    git init -b main | Out-Null
    Write-Host 'Repositorio git inicializado.'
} else {
    Write-Host 'Ya existía un repositorio git aquí; se reutiliza.'
}

# --- 2. Preparar el índice (aplica .gitignore) ---
git add -A

# --- 3. Guarda: nada de frontend/ en el commit ---
$staged = git diff --cached --name-only
$frontend = $staged | Where-Object { $_ -like 'frontend/*' }
if ($frontend) {
    Write-Host 'ABORTADO: hay archivos de frontend/ preparados para subir:' -ForegroundColor Red
    $frontend | ForEach-Object { Write-Host "   $_" }
    Write-Host 'Revisa que .gitignore contenga la línea  frontend/' -ForegroundColor Red
    exit 1
}

# --- 4. Guarda: ningún secreto (cadena de conexión con contraseña) ---
$leak = git grep --cached -F -n 'Password=' 2>$null
if ($leak) {
    Write-Host 'ABORTADO: se detectó una contraseña en los archivos a subir:' -ForegroundColor Red
    Write-Host $leak
    Write-Host 'appsettings.json no debe llevar la cadena de conexión. No se sube nada.' -ForegroundColor Red
    exit 1
}

# --- 5. Resumen de lo que se sube ---
$count = ($staged | Measure-Object).Count
Write-Host ''
Write-Host "Se subiran $count archivos. Primeros:" -ForegroundColor Cyan
$staged | Select-Object -First 30 | ForEach-Object { Write-Host "   $_" }
if ($count -gt 30) { Write-Host '   ...' }
Write-Host ''

# --- 6. Commit ---
git commit -m 'Backend WebhookGateway: API, despachador, datos y despliegue en Render' | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'No había nada nuevo que commitear (o el commit falló). Revisa el mensaje de arriba.' -ForegroundColor Yellow
}

# --- 7. Remoto ---
if ((git remote) -contains 'origin') {
    git remote set-url origin $RepoUrl
} else {
    git remote add origin $RepoUrl
}
git branch -M main

# --- 8. Push ---
Write-Host 'Subiendo a GitHub (puede pedirte iniciar sesión)...' -ForegroundColor Cyan
git push -u origin main
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host 'El push fue rechazado.' -ForegroundColor Yellow
    Write-Host 'Suele pasar si el repo remoto ya tenía commits (por ejemplo un README inicial).'
    Write-Host 'Ejecuta esto y vuelve a intentar:' -ForegroundColor Yellow
    Write-Host '   git pull --rebase origin main'
    Write-Host '   git push -u origin main'
    exit 1
}

Write-Host ''
Write-Host "Listo. Backend subido a $RepoUrl" -ForegroundColor Green
Write-Host 'Siguiente: Render -> New -> Blueprint apuntando a este repo (ver docs/despliegue.md).' -ForegroundColor Green
