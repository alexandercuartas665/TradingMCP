# ============================================================
# _base.ps1  --  Motor de descarga + validacion
# No ejecutar directamente. Llamar desde los scripts de activo.
# ============================================================
param(
    [Parameter(Mandatory)][string]$Symbol,
    [Parameter(Mandatory)][string]$Timeframe,
    [string]$Desde    = "2019-04-11",
    [string]$Hasta    = (Get-Date -Format "yyyy-MM-dd"),
    [int]$ChunkDias   = 4,
    [string]$Server   = "http://localhost:8765"
)

$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Invoke-Api($path) {
    try { return Invoke-RestMethod -Uri "$Server$path" -Method GET -TimeoutSec 60 }
    catch { return $null }
}

function Wait-Descarga([int]$MaxSegundos = 300) {
    $t = 0
    do {
        Start-Sleep -Seconds 5
        $t += 5
        $e = Invoke-Api "/api/historico/estado"
        if ($e -ne $null -and -not $e.esta_activo) { return $e }
    } while ($t -lt $MaxSegundos)
    return $null
}

# -- Verificar que el servidor esta activo -------------------
$ping = Invoke-Api "/api/indicadores"
if ($ping -eq $null) {
    Write-Host "ERROR: No se puede conectar al servidor en $Server" -ForegroundColor Red
    Write-Host "       Asegurate de que la aplicacion WpfDerivClientVB este corriendo." -ForegroundColor Yellow
    exit 1
}

# -- Calcular velas esperadas (estimacion) -------------------
$diasTotal = ([datetime]$Hasta - [datetime]$Desde).TotalDays
$minutesPorDia = switch ($Timeframe) {
    "1d"  { 1 }
    "4h"  { 6 }
    "1h"  { 24 }
    "15m" { 96 }
    "5m"  { 288 }
    "1m"  { 1440 }
    default { 24 }
}
$velaEsperadas = [int]($diasTotal * $minutesPorDia)

Write-Host ""
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "  $Symbol  |  $Timeframe  |  $Desde --> $Hasta" -ForegroundColor Cyan
Write-Host "  Dias solicitados: $([int]$diasTotal)  |  Velas estimadas: ~$velaEsperadas" -ForegroundColor DarkCyan
Write-Host "======================================================" -ForegroundColor Cyan

$totalVelas  = 0
$totalChunks = 0
$errores     = 0
$cursor      = [datetime]::ParseExact($Hasta,  "yyyy-MM-dd", $null)
$limite      = [datetime]::ParseExact($Desde,  "yyyy-MM-dd", $null)
$chunkActual = $ChunkDias

while ($cursor -gt $limite) {
    $chunkDesde = $cursor.AddDays(-$chunkActual)
    if ($chunkDesde -lt $limite) { $chunkDesde = $limite }

    $desdeStr = $chunkDesde.ToString("yyyy-MM-dd")
    $hastaStr = $cursor.ToString("yyyy-MM-dd")

    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $desdeStr --> $hastaStr ..." -NoNewline

    $url  = "/api/historico/descargar?symbol=$Symbol&timeframe=$([uri]::EscapeDataString($Timeframe))&desde=$desdeStr&hasta=$hastaStr"
    $resp = Invoke-Api $url

    if ($resp -eq $null -or -not $resp.ok) {
        Write-Host " ERROR al iniciar" -ForegroundColor Red
        $errores++
        $cursor = $chunkDesde
        Start-Sleep -Seconds 3
        continue
    }

    $estado = Wait-Descarga

    if ($estado -eq $null) {
        Write-Host " TIMEOUT" -ForegroundColor Yellow
        $errores++
        $cursor = $chunkDesde
        continue
    }

    if ($estado.tuvo_error) {
        Write-Host " FALLO: $($estado.mensaje)" -ForegroundColor Red
        $errores++
        if ($chunkActual -gt 2) {
            Write-Host "        Reintentando con chunk de 2 dias..." -ForegroundColor Yellow
            $chunkActual = 2
            continue
        }
    } else {
        $velas       = $estado.velas_guardadas
        $totalVelas += $velas
        $totalChunks++
        $chunkActual = $ChunkDias
        Write-Host " OK ($velas velas)" -ForegroundColor Green
    }

    $cursor = $chunkDesde
    Start-Sleep -Milliseconds 800
}

# -- Validacion final en BD ----------------------------------
Write-Host ""
Write-Host "Validando en base de datos..." -ForegroundColor DarkCyan
$contar = Invoke-Api "/api/historico/contar?symbol=$Symbol&timeframe=$([uri]::EscapeDataString($Timeframe))"
$enBD   = if ($contar -ne $null -and $contar.ok) { $contar.total_registros } else { "N/D" }

$pct = 0
if ($velaEsperadas -gt 0 -and $enBD -ne "N/D") {
    $pct = [int]([double]$enBD / $velaEsperadas * 100)
}
$pctColor = if ($pct -ge 80) { "Green" } elseif ($pct -ge 50) { "Yellow" } else { "Red" }

Write-Host ""
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "  RESUMEN: $Symbol $Timeframe"                         -ForegroundColor White
Write-Host "  Chunks completados : $totalChunks"                   -ForegroundColor Green
Write-Host "  Velas descargadas  : $totalVelas"                    -ForegroundColor Green
Write-Host "  Registros en BD    : $enBD"                          -ForegroundColor $(if ($enBD -gt 0) { "Green" } else { "Yellow" })
Write-Host "  Cobertura estimada : $pct %"                         -ForegroundColor $pctColor
if ($errores -gt 0) {
    Write-Host "  Chunks con error   : $errores"                   -ForegroundColor Yellow
}
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host ""
