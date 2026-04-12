# ============================================================
# descargar_masivo.ps1
#
# Descarga historico de todos los activos/temporalidades
# de forma pausada para no saturar la API de Deriv.
#
# Estrategia:
#   - Chunks de ChunkDias dias por peticion
#   - Pausa de DelayMinutos entre cada chunk
#   - Guarda progreso en progreso.json (puede parar y reanudar)
#
# Uso:
#   .\descargar_masivo.ps1
#   .\descargar_masivo.ps1 -DelayMinutos 15 -ChunkDias 4
#   .\descargar_masivo.ps1 -SoloActivo "frxEURUSD" -SoloTF "1h"
#   .\descargar_masivo.ps1 -Resetear   (borra progreso y empieza de cero)
# ============================================================
param(
    [int]$DelayMinutos  = 1,
    [int]$ChunkDias     = 5,
    [string]$Desde      = "2019-04-11",
    [string]$Hasta      = (Get-Date -Format "yyyy-MM-dd"),
    [string]$Server     = "http://localhost:8765",
    [string]$SoloActivo = "",
    [string]$SoloTF     = "",
    [switch]$Resetear
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Continue"
$ProgressFile = "$PSScriptRoot\progreso.json"

# ============================================================
# CATALOGO DE COMBINACIONES
# [?] = verificar simbolo exacto en Deriv antes de ejecutar
# ============================================================
$COMBINACIONES = @(
    # Forex
    @{ Id="EURUSD-1d"; Symbol="frxEURUSD"; TF="1d"; Nombre="EURUSD" }
    @{ Id="EURUSD-4h"; Symbol="frxEURUSD"; TF="4h"; Nombre="EURUSD" }
    @{ Id="EURUSD-1h"; Symbol="frxEURUSD"; TF="1h"; Nombre="EURUSD" }
    @{ Id="EURUSD-5m"; Symbol="frxEURUSD"; TF="5m"; Nombre="EURUSD" }
    @{ Id="GBPUSD-1d"; Symbol="frxGBPUSD"; TF="1d"; Nombre="GBPUSD" }
    @{ Id="GBPUSD-4h"; Symbol="frxGBPUSD"; TF="4h"; Nombre="GBPUSD" }
    @{ Id="GBPUSD-1h"; Symbol="frxGBPUSD"; TF="1h"; Nombre="GBPUSD" }
    @{ Id="GBPUSD-5m"; Symbol="frxGBPUSD"; TF="5m"; Nombre="GBPUSD" }
    @{ Id="USDCHF-1d"; Symbol="frxUSDCHF"; TF="1d"; Nombre="USDCHF" }
    @{ Id="USDCHF-4h"; Symbol="frxUSDCHF"; TF="4h"; Nombre="USDCHF" }
    @{ Id="USDCHF-1h"; Symbol="frxUSDCHF"; TF="1h"; Nombre="USDCHF" }
    @{ Id="USDCHF-5m"; Symbol="frxUSDCHF"; TF="5m"; Nombre="USDCHF" }
    @{ Id="USDJPY-1d"; Symbol="frxUSDJPY"; TF="1d"; Nombre="USDJPY" }
    @{ Id="USDJPY-4h"; Symbol="frxUSDJPY"; TF="4h"; Nombre="USDJPY" }
    @{ Id="USDJPY-1h"; Symbol="frxUSDJPY"; TF="1h"; Nombre="USDJPY" }
    @{ Id="USDJPY-5m"; Symbol="frxUSDJPY"; TF="5m"; Nombre="USDJPY" }
    @{ Id="GBPJPY-1d"; Symbol="frxGBPJPY"; TF="1d"; Nombre="GBPJPY" }
    @{ Id="GBPJPY-4h"; Symbol="frxGBPJPY"; TF="4h"; Nombre="GBPJPY" }
    @{ Id="GBPJPY-1h"; Symbol="frxGBPJPY"; TF="1h"; Nombre="GBPJPY" }
    @{ Id="GBPJPY-5m"; Symbol="frxGBPJPY"; TF="5m"; Nombre="GBPJPY" }
    # Metales
    @{ Id="XAUUSD-1d"; Symbol="frxXAUUSD"; TF="1d"; Nombre="XAUUSD (Oro)" }
    @{ Id="XAUUSD-4h"; Symbol="frxXAUUSD"; TF="4h"; Nombre="XAUUSD (Oro)" }
    @{ Id="XAUUSD-1h"; Symbol="frxXAUUSD"; TF="1h"; Nombre="XAUUSD (Oro)" }
    @{ Id="XAUUSD-5m"; Symbol="frxXAUUSD"; TF="5m"; Nombre="XAUUSD (Oro)" }
    @{ Id="XAGUSD-1d"; Symbol="frxXAGUSD"; TF="1d"; Nombre="XAGUSD (Plata)" }
    @{ Id="XAGUSD-4h"; Symbol="frxXAGUSD"; TF="4h"; Nombre="XAGUSD (Plata)" }
    @{ Id="XAGUSD-1h"; Symbol="frxXAGUSD"; TF="1h"; Nombre="XAGUSD (Plata)" }
    @{ Id="XAGUSD-5m"; Symbol="frxXAGUSD"; TF="5m"; Nombre="XAGUSD (Plata)" }
    # Commodities [?] verificar simbolo
    @{ Id="OIL-1d";    Symbol="frxUSOIL";  TF="1d"; Nombre="OIL_US [?]" }
    @{ Id="OIL-4h";    Symbol="frxUSOIL";  TF="4h"; Nombre="OIL_US [?]" }
    @{ Id="OIL-1h";    Symbol="frxUSOIL";  TF="1h"; Nombre="OIL_US [?]" }
    @{ Id="OIL-5m";    Symbol="frxUSOIL";  TF="5m"; Nombre="OIL_US [?]" }
    # Indices [?] verificar simbolo
    @{ Id="NAS100-1d"; Symbol="OTC_NDX";   TF="1d"; Nombre="NAS100 [?]" }
    @{ Id="NAS100-4h"; Symbol="OTC_NDX";   TF="4h"; Nombre="NAS100 [?]" }
    @{ Id="NAS100-1h"; Symbol="OTC_NDX";   TF="1h"; Nombre="NAS100 [?]" }
    @{ Id="NAS100-5m"; Symbol="OTC_NDX";   TF="5m"; Nombre="NAS100 [?]" }
    @{ Id="SP500-1d";  Symbol="OTC_SPC";   TF="1d"; Nombre="SP500 [?]" }
    @{ Id="SP500-4h";  Symbol="OTC_SPC";   TF="4h"; Nombre="SP500 [?]" }
    @{ Id="SP500-1h";  Symbol="OTC_SPC";   TF="1h"; Nombre="SP500 [?]" }
    @{ Id="SP500-5m";  Symbol="OTC_SPC";   TF="5m"; Nombre="SP500 [?]" }
)

# ============================================================
# FUNCIONES
# ============================================================

function Invoke-Api($path) {
    try { return Invoke-RestMethod -Uri "$Server$path" -Method GET -TimeoutSec 60 }
    catch { return $null }
}

function Wait-Chunk([int]$MaxSeg = 300) {
    $t = 0
    do {
        Start-Sleep -Seconds 5
        $t += 5
        $e = Invoke-Api "/api/historico/estado"
        if ($e -ne $null -and -not $e.esta_activo) { return $e }
    } while ($t -lt $MaxSeg)
    return $null
}

function Load-Progreso {
    if (Test-Path $ProgressFile) {
        try { return Get-Content $ProgressFile -Raw | ConvertFrom-Json }
        catch { return @{} }
    }
    return @{}
}

function Save-Progreso($prog) {
    $prog | ConvertTo-Json | Set-Content $ProgressFile -Encoding UTF8
}

function Format-Eta($chunksRestantes, $delayMin) {
    $minutos = $chunksRestantes * $delayMin
    if ($minutos -lt 60)   { return "$minutos min" }
    if ($minutos -lt 1440) { return "$([int]($minutos/60)) h $($minutos % 60) min" }
    return "$([int]($minutos/1440)) dias $([int](($minutos % 1440)/60)) h"
}

# ============================================================
# INICIO
# ============================================================

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  DESCARGA MASIVA HISTORICA" -ForegroundColor Cyan
Write-Host "  Chunks: $ChunkDias dias | Pausa: $DelayMinutos min entre chunks" -ForegroundColor Cyan
Write-Host "  Periodo: $Desde --> $Hasta" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# -- Verificar servidor --
$ping = Invoke-Api "/api/indicadores"
if ($ping -eq $null) {
    Write-Host ""
    Write-Host "[ERROR] No se puede conectar al servidor $Server" -ForegroundColor Red
    Write-Host "        Abre la aplicacion WpfDerivClientVB primero." -ForegroundColor Yellow
    exit 1
}
Write-Host "  Servidor: OK" -ForegroundColor Green

# -- Resetear progreso si se pide --
if ($Resetear -and (Test-Path $ProgressFile)) {
    Remove-Item $ProgressFile -Force
    Write-Host "  Progreso reiniciado." -ForegroundColor Yellow
}

# -- Filtrar combinaciones --
$combos = $COMBINACIONES
if ($SoloActivo -ne "") { $combos = $combos | Where-Object { $_.Symbol -eq $SoloActivo } }
if ($SoloTF     -ne "") { $combos = $combos | Where-Object { $_.TF     -eq $SoloTF } }

Write-Host "  Combinaciones activas: $($combos.Count)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# ============================================================
# BUCLE PRINCIPAL
# ============================================================

$fechaLimite = [datetime]::ParseExact($Desde, "yyyy-MM-dd", $null)
$progreso    = Load-Progreso

$totalChunksOk  = 0
$totalVelas     = 0
$totalErrores   = 0

foreach ($combo in $combos) {
    $id = $combo.Id

    # -- Determinar cursor (punto de continuacion) --
    $cursorStr = if ($progreso.PSObject.Properties[$id]) { $progreso.$id } else { $Hasta }
    $cursor    = [datetime]::ParseExact($cursorStr, "yyyy-MM-dd", $null)

    if ($cursor -le $fechaLimite) {
        Write-Host "[$id] Ya completo - saltando." -ForegroundColor DarkGray
        continue
    }

    # Estimar chunks restantes para ETA
    $diasRestantes   = ($cursor - $fechaLimite).TotalDays
    $chunksRestantes = [int][Math]::Ceiling($diasRestantes / $ChunkDias)

    Write-Host "--------------------------------------------------------------" -ForegroundColor DarkCyan
    Write-Host "  $($combo.Nombre) | $($combo.TF) | Desde $cursorStr hasta $Desde" -ForegroundColor White
    Write-Host "  Chunks restantes: ~$chunksRestantes | ETA: ~$(Format-Eta $chunksRestantes $DelayMinutos)" -ForegroundColor DarkCyan
    Write-Host "--------------------------------------------------------------" -ForegroundColor DarkCyan

    while ($cursor -gt $fechaLimite) {
        $chunkDesde = $cursor.AddDays(-$ChunkDias)
        if ($chunkDesde -lt $fechaLimite) { $chunkDesde = $fechaLimite }

        $desdeStr = $chunkDesde.ToString("yyyy-MM-dd")
        $hastaStr = $cursor.ToString("yyyy-MM-dd")

        $hora = Get-Date -Format "HH:mm:ss"
        Write-Host "[$hora] $id  $desdeStr --> $hastaStr ..." -NoNewline

        # -- Lanzar descarga --
        $url  = "/api/historico/descargar?symbol=$($combo.Symbol)&timeframe=$([uri]::EscapeDataString($combo.TF))&desde=$desdeStr&hasta=$hastaStr"
        $resp = Invoke-Api $url

        if ($resp -eq $null -or -not $resp.ok) {
            Write-Host " [ERROR al iniciar]" -ForegroundColor Red
            $totalErrores++
            $cursor = $chunkDesde
            continue
        }

        # -- Esperar resultado --
        $estado = Wait-Chunk

        if ($estado -eq $null) {
            Write-Host " [TIMEOUT]" -ForegroundColor Yellow
            $totalErrores++
        } elseif ($estado.tuvo_error) {
            Write-Host " [FALLO] $($estado.mensaje)" -ForegroundColor Red
            $totalErrores++
        } else {
            $velas = $estado.velas_guardadas
            $totalVelas    += $velas
            $totalChunksOk++
            Write-Host " OK  ($velas velas)" -ForegroundColor Green
        }

        # -- Avanzar cursor y guardar progreso --
        $cursor = $chunkDesde
        if ($progreso.PSObject.Properties[$id]) {
            $progreso.$id = $cursor.ToString("yyyy-MM-dd")
        } else {
            $progreso | Add-Member -NotePropertyName $id -NotePropertyValue $cursor.ToString("yyyy-MM-dd") -Force
        }
        Save-Progreso $progreso

        # -- Pausa entre chunks --
        if ($cursor -gt $fechaLimite) {
            $proxima = (Get-Date).AddMinutes($DelayMinutos).ToString("HH:mm:ss")
            Write-Host "    Esperando $DelayMinutos min... (proxima peticion a las $proxima)" -ForegroundColor DarkGray
            Start-Sleep -Seconds ($DelayMinutos * 60)
        }
    }

    # Marcar combinacion completa
    if ($progreso.PSObject.Properties[$id]) {
        $progreso.$id = $Desde
    } else {
        $progreso | Add-Member -NotePropertyName $id -NotePropertyValue $Desde -Force
    }
    Save-Progreso $progreso

    Write-Host "  [COMPLETO] $id" -ForegroundColor Green
    Write-Host ""
}

# ============================================================
# RESUMEN FINAL
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  SESION COMPLETADA" -ForegroundColor Green
Write-Host "  Chunks OK    : $totalChunksOk" -ForegroundColor Green
Write-Host "  Velas nuevas : $totalVelas" -ForegroundColor Green
if ($totalErrores -gt 0) {
    Write-Host "  Errores      : $totalErrores" -ForegroundColor Yellow
}
Write-Host "  Progreso guardado en: $ProgressFile" -ForegroundColor DarkGray
Write-Host "============================================================" -ForegroundColor Cyan
