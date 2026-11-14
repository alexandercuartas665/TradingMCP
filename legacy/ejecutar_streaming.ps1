# Script de PowerShell para escuchar precios en tiempo real

param(
    [string]$Symbol = "",
    [string]$Timeframe = ""
)

Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host "    Deriv Streaming en Vivo (Velas)                    " -ForegroundColor Cyan
Write-Host "=======================================================" -ForegroundColor Cyan

if (-not $Symbol) {
    Write-Host ""
    $Symbol = Read-Host "=> Ingresa el simbolo del activo (Ej: R_100, frxEURUSD)"
}

if (-not $Timeframe) {
    Write-Host "Opciones: 1m, 2m, 3m, 5m, 10m, 15m, 30m, 1h, 1d" -ForegroundColor DarkGray
    $Timeframe = Read-Host "=> Ingresa la temporalidad de la vela a escuchar"
}

Write-Host ""
Write-Host "[*] Iniciando streaming infinito para $Symbol ($Timeframe)..." -ForegroundColor Yellow

# Ejecutar script python infinito
python .\stream_candles.py --symbol $Symbol --timeframe $Timeframe

Write-Host ""
Write-Host "[*] Consola liberada." -ForegroundColor Cyan
Pause
