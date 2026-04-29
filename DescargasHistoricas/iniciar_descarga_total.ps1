# ============================================================
# iniciar_descarga_total.ps1  (v2)
#
# Lanza en segundo plano la descarga completa de datos historicos:
#   1) importar_historico.ps1  -> Yahoo Finance (1d/10y, 1h/730d, 4h agregado)
#   2) descargar_masivo.ps1    -> Deriv API     (1d/4h/1h/5m ultimos ~380 dias)
#
# Al finalizar ambas descargas muestra el reporte de cobertura.
#
# Uso:
#   .\iniciar_descarga_total.ps1                       (ejecuta todo)
#   .\iniciar_descarga_total.ps1 -SoloHistorico        (solo Yahoo Finance)
#   .\iniciar_descarga_total.ps1 -SoloReciente         (solo Deriv)
#   .\iniciar_descarga_total.ps1 -SoloActivo NAS100    (un activo)
#   .\iniciar_descarga_total.ps1 -MostrarLog           (ver log en tiempo real)
#   .\iniciar_descarga_total.ps1 -Servidor http://localhost:8765
# ============================================================
param(
    [switch]$SoloHistorico,
    [switch]$SoloReciente,
    [string]$SoloActivo  = "",
    [string]$Servidor    = "http://localhost:8765",
    [switch]$MostrarLog,
    [switch]$NoReporte
)

$ErrorActionPreference = "Continue"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$Root  = $PSScriptRoot
$Logs  = Join-Path $Root "logs"
$Stamp = Get-Date -Format "yyyyMMdd_HHmmss"

if (-not (Test-Path $Logs)) { New-Item -ItemType Directory -Path $Logs | Out-Null }

# ============================================================
# Verificar que el servidor este disponible antes de iniciar
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  DESCARGA TOTAL EN SEGUNDO PLANO" -ForegroundColor Cyan
Write-Host "  Servidor: $Servidor" -ForegroundColor Cyan
Write-Host "  Logs en : $Logs\" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

try {
    $ping = Invoke-RestMethod -Uri "$Servidor/api/indicadores" -Method GET -TimeoutSec 5 -ErrorAction Stop
    Write-Host "  [OK] Servidor disponible." -ForegroundColor Green
} catch {
    Write-Host "  [ADVERTENCIA] Servidor en $Servidor no responde." -ForegroundColor Yellow
    Write-Host "                descargar_masivo.ps1 fallara sin el servidor." -ForegroundColor Yellow
    Write-Host "                importar_historico.ps1 usara catalogo de respaldo." -ForegroundColor Yellow
    Write-Host ""
}

# ============================================================
# 1) Datos historicos (Yahoo Finance via importar_historico.ps1)
# ============================================================
$procH = $null
$logH  = $null

if (-not $SoloReciente) {
    $logH  = Join-Path $Logs "historico_${Stamp}.log"
    $argsH = @("-NonInteractive", "-NoProfile", "-File", "`"$Root\importar_historico.ps1`"",
                "-Servidor", $Servidor)
    if ($SoloActivo -ne "") { $argsH += "-SoloActivo"; $argsH += $SoloActivo }

    Write-Host "  [1/2] Historico Yahoo Finance -> $([IO.Path]::GetFileName($logH))" -ForegroundColor Yellow

    $procH = Start-Process pwsh `
        -ArgumentList $argsH `
        -RedirectStandardOutput $logH `
        -RedirectStandardError  ($logH -replace "\.log$","_err.log") `
        -NoNewWindow -PassThru

    Write-Host "        PID: $($procH.Id)" -ForegroundColor DarkGray
}

# ============================================================
# 2) Datos recientes (Deriv via descargar_masivo.ps1)
# ============================================================
$procR = $null
$logR  = $null

if (-not $SoloHistorico) {
    if (-not $SoloReciente) { Start-Sleep -Seconds 3 }

    $logR  = Join-Path $Logs "reciente_${Stamp}.log"
    $argsR = @("-NonInteractive", "-NoProfile", "-File", "`"$Root\descargar_masivo.ps1`"",
                "-Server", $Servidor)
    if ($SoloActivo -ne "") { $argsR += "-SoloActivo"; $argsR += $SoloActivo }

    Write-Host "  [2/2] Reciente Deriv API     -> $([IO.Path]::GetFileName($logR))" -ForegroundColor Yellow

    $procR = Start-Process pwsh `
        -ArgumentList $argsR `
        -RedirectStandardOutput $logR `
        -RedirectStandardError  ($logR -replace "\.log$","_err.log") `
        -NoNewWindow -PassThru

    Write-Host "        PID: $($procR.Id)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "  Los procesos corren en segundo plano." -ForegroundColor Green
Write-Host ""
Write-Host "  Ver progreso en tiempo real:" -ForegroundColor White
if ($logH) { Write-Host "    Get-Content `"$logH`" -Wait" -ForegroundColor DarkCyan }
if ($logR) { Write-Host "    Get-Content `"$logR`" -Wait" -ForegroundColor DarkCyan }
Write-Host ""

# ============================================================
# Mostrar log en tiempo real si se pidio
# ============================================================
if ($MostrarLog -and $logR) {
    Write-Host "  [MostrarLog] Siguiendo log de Deriv (Ctrl+C para salir)..." -ForegroundColor Cyan
    Start-Sleep -Seconds 5
    Get-Content $logR -Wait
}

# ============================================================
# Esperar ambos procesos y mostrar reporte al final
# ============================================================
if (-not $NoReporte) {
    Write-Host "  Esperando que terminen los procesos para generar reporte..." -ForegroundColor DarkGray

    $procs = @()
    if ($procH -ne $null) { $procs += $procH }
    if ($procR -ne $null) { $procs += $procR }

    foreach ($p in $procs) {
        try { $p.WaitForExit(3600000) | Out-Null } catch {}   # max 1 hora
    }

    Write-Host ""
    Write-Host "  Ambas descargas completadas. Generando reporte..." -ForegroundColor Green
    Write-Host ""

    & "$Root\reporte_bd.ps1" -Servidor $Servidor
}

# ============================================================
# Estado de salida
# ============================================================
Start-Sleep -Seconds 2
$ok = $true
if ($procH -and $procH.HasExited -and $procH.ExitCode -ne 0) {
    Write-Host "  [ADVERTENCIA] Proceso historico termino con codigo $($procH.ExitCode)" -ForegroundColor Red
    $ok = $false
}
if ($procR -and $procR.HasExited -and $procR.ExitCode -ne 0) {
    Write-Host "  [ADVERTENCIA] Proceso Deriv termino con codigo $($procR.ExitCode)" -ForegroundColor Red
    $ok = $false
}
if ($ok) {
    Write-Host "  [OK] Procesos iniciados correctamente." -ForegroundColor Green
}
Write-Host ""
