# ============================================================
# run_backtest.ps1
# Uso desde ISE o consola:
#   . .\run_backtest.ps1          <- cargar funcion (dot-source)
#   Invoke-Backtest               <- defaults
#   Invoke-Backtest -Nombre BollingerRsiReversal -Confianza 60
# ============================================================

function Invoke-Backtest {
    param(
        [string]  $Nombre     = "EmaRsiCruce",
        [string]  $Symbol     = "R_100",
        [string]  $Timeframe  = "1m",
        [int]     $Duracion   = 5,
        [decimal] $Payout     = 85,
        [int]     $Confianza  = 55,
        [decimal] $Monto      = 10,
        [int]     $Ventana    = 100,
        [long]    $Desde      = 0,
        [long]    $Hasta      = 0,
        [string]  $Servidor   = "http://localhost:8765"
    )

    # -- Construir URL -----------------------------------------------
    $url  = "$Servidor/api/dinamicas/backtest"
    $url += "?nombre=$([Uri]::EscapeDataString($Nombre))"
    $url += "&symbol=$Symbol"
    $url += "&timeframe=$Timeframe"
    $url += "&duracion_velas=$Duracion"
    $url += "&payout=$Payout"
    $url += "&min_confianza=$Confianza"
    $url += "&monto=$Monto"
    $url += "&ventana=$Ventana"
    if ($Desde -gt 0) { $url += "&desde_epoch=$Desde" }
    if ($Hasta -gt 0) { $url += "&hasta_epoch=$Hasta"  }

    Write-Host ""
    Write-Host "=====================================================" -ForegroundColor Cyan
    Write-Host "  BACKTEST: $Nombre" -ForegroundColor Yellow
    Write-Host "=====================================================" -ForegroundColor Cyan
    Write-Host "  Activo    : $Symbol / $Timeframe"
    Write-Host "  Duracion  : $Duracion velas    Ventana: $Ventana velas"
    Write-Host "  Payout    : $Payout%           Confianza min: $Confianza%"
    Write-Host "  Monto     : `$$Monto"
    if ($Desde -gt 0) {
        Write-Host ("  Desde     : {0} ({1})" -f [DateTimeOffset]::FromUnixTimeSeconds($Desde).ToString("dd/MM/yyyy HH:mm"), $Desde)
    }
    if ($Hasta -gt 0) {
        Write-Host ("  Hasta     : {0} ({1})" -f [DateTimeOffset]::FromUnixTimeSeconds($Hasta).ToString("dd/MM/yyyy HH:mm"), $Hasta)
    }
    Write-Host "-----------------------------------------------------" -ForegroundColor DarkGray
    Write-Host "  Ejecutando..." -ForegroundColor Gray

    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 600
        $sw.Stop()
        $json = $resp.Content | ConvertFrom-Json

        if ($json.ok -eq $true) {
            $wr   = [math]::Round($json.win_rate * 100, 2)
            $pf   = [math]::Round($json.profit_factor, 4)
            $pnl  = $json.profit_loss
            $dd   = [math]::Round($json.max_drawdown, 4)
            $secs = [math]::Round($sw.Elapsed.TotalSeconds, 1)

            Write-Host ""
            Write-Host "  RESULTADO  (sesion #$($json.sesion_id))" -ForegroundColor Green
            Write-Host "-----------------------------------------------------" -ForegroundColor DarkGray
            Write-Host ("  Senales        : {0:N0}"  -f $json.total_senales)
            Write-Host ("  Operaciones    : {0:N0}"  -f $json.total_ops)
            Write-Host ("  Ganadas        : {0:N0}"  -f $json.ganadas)
            Write-Host ("  Perdidas       : {0:N0}"  -f $json.perdidas)
            Write-Host ""

            $wrColor  = if ($wr  -ge 54.05) { "Green" } else { "Red" }
            $pnlColor = if ($pnl -ge 0)     { "Green" } else { "Red" }
            $pfColor  = if ($pf  -ge 1)     { "Green" } else { "Red" }

            Write-Host ("  Win Rate       : {0}%"   -f $wr)  -ForegroundColor $wrColor
            Write-Host ("  Profit / Loss  : `${0:N2}" -f $pnl) -ForegroundColor $pnlColor
            Write-Host ("  Profit Factor  : {0}"    -f $pf)  -ForegroundColor $pfColor
            Write-Host ("  Max Drawdown   : {0}%"   -f $dd)  -ForegroundColor Yellow
            Write-Host ""
            Write-Host ("  Tiempo         : {0}s"   -f $secs) -ForegroundColor Gray
            Write-Host "=====================================================" -ForegroundColor Cyan
            Write-Host ""

            return $json

        } else {
            Write-Host ""
            Write-Host "  ERROR: $($json.error)" -ForegroundColor Red
            Write-Host "=====================================================" -ForegroundColor Cyan
        }

    } catch {
        $sw.Stop()
        Write-Host ""
        Write-Host "  EXCEPCION: $($_.Exception.Message)" -ForegroundColor Red
        if ($_.Exception.Message -like "*tiempo*" -or $_.Exception.Message -like "*timeout*") {
            Write-Host "  El backtest sigue corriendo en el servidor (>10 min)." -ForegroundColor Yellow
        }
        Write-Host "=====================================================" -ForegroundColor Cyan
    }
}

# Alias corto opcional
Set-Alias -Name bt -Value Invoke-Backtest -Scope Global

Write-Host "Funcion cargada: Invoke-Backtest  (alias: bt)" -ForegroundColor Green
Write-Host "Ejemplos:" -ForegroundColor Gray
Write-Host "  Invoke-Backtest" -ForegroundColor DarkCyan
Write-Host "  Invoke-Backtest -Nombre BollingerRsiReversal -Confianza 60" -ForegroundColor DarkCyan
Write-Host "  bt -Nombre DonchianMacd -Symbol R_100 -Timeframe 5m" -ForegroundColor DarkCyan
Write-Host ""
