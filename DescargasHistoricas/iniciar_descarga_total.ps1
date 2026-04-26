# ============================================================
# iniciar_descarga_total.ps1
#
# Lanza en segundo plano la descarga completa de datos historicos:
#   1) importar_historico.ps1  -> Yahoo Finance (1d/10y, 1h/730d, 4h agregado)
#   2) descargar_masivo.ps1    -> Deriv API    (1d/1h/4h/5m ultimos ~380 dias)
#
# Uso:
#   .\iniciar_descarga_total.ps1                   (ejecuta todo)
#   .\iniciar_descarga_total.ps1 -SoloHistorico    (solo Yahoo Finance)
#   .\iniciar_descarga_total.ps1 -SoloReciente     (solo Deriv)
#   .\iniciar_descarga_total.ps1 -SoloActivo EURUSD
#
# Los resultados se guardan en .\logs\
# ============================================================
param(
    [switch]$SoloHistorico,
    [switch]$SoloReciente,
    [string]$SoloActivo = ""
)

$ErrorActionPreference = "Continue"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$Root  = $PSScriptRoot
$Logs  = Join-Path $Root "logs"
$Stamp = Get-Date -Format "yyyyMMdd_HHmmss"

if (-not (Test-Path $Logs)) { New-Item -ItemType Directory -Path $Logs | Out-Null }

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  INICIANDO DESCARGA TOTAL EN SEGUNDO PLANO" -ForegroundColor Cyan
Write-Host "  Logs en: $Logs\" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# ---- 1) Datos historicos (Yahoo Finance) --------------------------------
if (-not $SoloReciente) {
    $logH = Join-Path $Logs "historico_${Stamp}.log"

    $argsH = @("-NonInteractive", "-NoProfile", "-File", "`"$Root\importar_historico.ps1`"")
    if ($SoloActivo -ne "") { $argsH += "-SoloActivo"; $argsH += $SoloActivo }

    Write-Host "  [1/2] Historico Yahoo Finance -> $([System.IO.Path]::GetFileName($logH))" -ForegroundColor Yellow

    $procH = Start-Process pwsh `
        -ArgumentList $argsH `
        -RedirectStandardOutput $logH `
        -RedirectStandardError  ($logH -replace "\.log$","_err.log") `
        -NoNewWindow `
        -PassThru

    Write-Host "        PID: $($procH.Id)" -ForegroundColor DarkGray
}

# ---- 2) Datos recientes (Deriv via descargar_masivo.ps1) ----------------
if (-not $SoloHistorico) {
    # Pequena pausa para no saturar si ambos corren a la vez
    if (-not $SoloReciente) { Start-Sleep -Seconds 3 }

    $logR = Join-Path $Logs "reciente_${Stamp}.log"

    $argsR = @("-NonInteractive", "-NoProfile", "-File", "`"$Root\descargar_masivo.ps1`"")

    Write-Host "  [2/2] Reciente Deriv API    -> $([System.IO.Path]::GetFileName($logR))" -ForegroundColor Yellow

    $procR = Start-Process pwsh `
        -ArgumentList $argsR `
        -RedirectStandardOutput $logR `
        -RedirectStandardError  ($logR -replace "\.log$","_err.log") `
        -NoNewWindow `
        -PassThru

    Write-Host "        PID: $($procR.Id)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "  Los procesos corren en segundo plano." -ForegroundColor Green
Write-Host "  Para ver progreso en tiempo real:" -ForegroundColor White

if (-not $SoloReciente) {
    Write-Host "    Get-Content `"$logH`" -Wait" -ForegroundColor DarkCyan
}
if (-not $SoloHistorico) {
    Write-Host "    Get-Content `"$logR`" -Wait" -ForegroundColor DarkCyan
}

Write-Host ""
Write-Host "  Para ver un resumen de cobertura al terminar:" -ForegroundColor White
Write-Host "    .\reporte_bd.ps1" -ForegroundColor DarkCyan
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan

# Esperar un poco y mostrar si los procesos arrancaron bien
Start-Sleep -Seconds 2
$ok = $true
if (-not $SoloReciente -and $procH -and $procH.HasExited -and $procH.ExitCode -ne 0) {
    Write-Host "  [ADVERTENCIA] El proceso historico termino con error $($procH.ExitCode)" -ForegroundColor Red
    $ok = $false
}
if (-not $SoloHistorico -and $procR -and $procR.HasExited -and $procR.ExitCode -ne 0) {
    Write-Host "  [ADVERTENCIA] El proceso reciente termino con error $($procR.ExitCode)" -ForegroundColor Red
    $ok = $false
}
if ($ok) {
    Write-Host "  [OK] Procesos iniciados correctamente. Puedes cerrar esta ventana." -ForegroundColor Green
}
Write-Host ""
