# Script interactivo de PowerShell para consultar el historial de precios (velas/senales)

param(
    [string]$Symbol = "",
    [string]$Timeframe = "",
    [int]$Count = 0
)

Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host "    Consultor Historico de Velas - Deriv API        " -ForegroundColor Cyan
Write-Host "=======================================================" -ForegroundColor Cyan

# Comprobar instalacion de Python rapida
$pythonExists = Get-Command "python" -ErrorAction SilentlyContinue
if (-not $pythonExists) {
    Write-Host "[X] ERROR: Python no esta instalado o en el PATH." -ForegroundColor Red
    Exit
}

# 1. Solicitar el simbolo si no se envio por comando
if (-not $Symbol) {
    Write-Host ""
    $Symbol = Read-Host "=> Ingresa el simbolo del activo (Ej: R_100, R_50, frxEURUSD)"
}

# 2. Solicitar el timeframe si no se envio por comando
if (-not $Timeframe) {
    Write-Host "Opciones validas: 1m, 2m, 3m, 5m, 10m, 15m, 30m, 1h, 2h, 4h, 8h, 1d" -ForegroundColor DarkGray
    $Timeframe = Read-Host "=> Ingresa la temporalidad"
}

# 3. Solicitar la cantidad
if ($Count -eq 0) {
    $countInput = Read-Host "=> Cantidad de velas historicas a mostrar (Presiona ENTER para usar 15)"
    if ($countInput -match "^\d+$") {
        $Count = [int]$countInput
    } else {
        $Count = 15
    }
}

Write-Host ""
Write-Host "[*] Procesando solicitud de $Count velas de $Timeframe para $Symbol..." -ForegroundColor Yellow

# Ejecutar el script python y pasarle los parametros
python .\get_candles.py --symbol $Symbol --timeframe $Timeframe --count $Count

Write-Host ""
Write-Host "[*] Ejecucion terminada." -ForegroundColor Cyan
Pause
