# ============================================================
# descargar_historico_auto.ps1
# Descarga automatica de historico por chunks de 4 dias
# Uso: .\descargar_historico_auto.ps1 -Symbol R_100 -Timeframe 1m -Desde 2025-04-11 -Hasta 2025-12-24
# ============================================================
param(
    [string]$Symbol    = "R_100",
    [string]$Timeframe = "1m",
    [string]$Desde     = "2025-04-11",
    [string]$Hasta     = "2025-12-24",
    [int]$ChunkDias    = 4,
    [string]$Server    = "http://localhost:8765"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Invoke-Api($path) {
    try { return Invoke-RestMethod -Uri "$Server$path" -Method GET -TimeoutSec 60 }
    catch { return $null }
}

$fechaFin    = [datetime]::ParseExact($Hasta, "yyyy-MM-dd", $null)
$fechaLimite = [datetime]::ParseExact($Desde, "yyyy-MM-dd", $null)
$chunk       = $ChunkDias
$totalVelas  = 0
$totalChunks = 0
$errores     = 0

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Descarga automatica: $Symbol $Timeframe" -ForegroundColor Cyan
Write-Host "  Desde: $Desde  Hasta: $Hasta" -ForegroundColor Cyan
Write-Host "  Chunks de $chunk dias" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$cursor = $fechaFin

while ($cursor -gt $fechaLimite) {
    $chunkDesde = $cursor.AddDays(-$chunk)
    if ($chunkDesde -lt $fechaLimite) { $chunkDesde = $fechaLimite }

    $desdeStr = $chunkDesde.ToString("yyyy-MM-dd")
    $hastaStr = $cursor.ToString("yyyy-MM-dd")

    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Descargando $desdeStr → $hastaStr ..." -NoNewline

    # Iniciar descarga
    $url  = "/api/historico/descargar?symbol=$Symbol&timeframe=$([uri]::EscapeDataString($Timeframe))&desde=$desdeStr&hasta=$hastaStr"
    $resp = Invoke-Api $url

    if ($resp -eq $null -or -not $resp.ok) {
        Write-Host " ERROR al iniciar" -ForegroundColor Red
        $errores++
        $cursor = $chunkDesde
        Start-Sleep -Seconds 5
        continue
    }

    # Esperar a que complete (max 3 minutos)
    $intentos = 0
    $completado = $false
    do {
        Start-Sleep -Seconds 5
        $estado = Invoke-Api "/api/historico/estado"
        $intentos++

        if ($estado -ne $null -and -not $estado.esta_activo) {
            $completado = $true
        }
    } while (-not $completado -and $intentos -lt 36)

    if (-not $completado) {
        Write-Host " TIMEOUT" -ForegroundColor Yellow
        $errores++
    } elseif ($estado.tuvo_error) {
        Write-Host " FALLO: $($estado.mensaje)" -ForegroundColor Red
        $errores++
        # Reintentar con chunk más pequeño
        if ($chunk -gt 2) {
            Write-Host "         Reintentando con chunk de 2 dias..." -ForegroundColor Yellow
            $chunk = 2
            continue
        }
    } else {
        $velas = $estado.velas_guardadas
        $totalVelas  += $velas
        $totalChunks++
        $chunk = $ChunkDias  # restaurar tamaño original si se habia reducido
        Write-Host " OK ($velas velas)" -ForegroundColor Green
    }

    $cursor = $chunkDesde
    Start-Sleep -Seconds 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  COMPLETADO" -ForegroundColor Green
Write-Host "  Chunks exitosos : $totalChunks" -ForegroundColor Green
Write-Host "  Velas descargadas: $totalVelas" -ForegroundColor Green
if ($errores -gt 0) {
    Write-Host "  Chunks con error : $errores" -ForegroundColor Yellow
}
Write-Host "========================================" -ForegroundColor Cyan
