# ============================================================
# run_nuevas_estrategias.ps1
# Backtest de las 3 nuevas estrategias sobre activos problematicos:
#
#   BollingerMomentum_v1  -> frxEURUSD, frxGBPJPY
#   ADXReversal_v1        -> frxGBPJPY, OTC_DJI, frxXAUUSD, frxUSDCHF, frxGBPUSD
#   CCIReversal_v1        -> OTC_DJI, frxXAUUSD
#
# Grid: 2 timeframes x 2 duraciones x 2 confianzas = 8 combos por activo
# Total: 72 backtests
# ============================================================
param(
    [string] $Servidor = "http://localhost:8765",
    [decimal]$Payout   = 85,
    [decimal]$Monto    = 10,
    [int]    $Ventana  = 100
)

$ErrorActionPreference = "Continue"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

# ---- GRIDS POR ESTRATEGIA ----
$GRIDS = @(
    @{
        Estrategia = "BollingerMomentum_v1"
        Etiqueta   = "Momentum (inverso reversal)"
        Activos    = @(
            @{ Nombre="EURUSD"; Symbol="frxEURUSD" },
            @{ Nombre="GBPJPY"; Symbol="frxGBPJPY" }
        )
        Timeframes  = @("1h","4h")
        Duraciones  = @(1, 2)
        Confianzas  = @(65, 70)
    },
    @{
        Estrategia = "ADXReversal_v1"
        Etiqueta   = "Reversal con filtro ADX<25"
        Activos    = @(
            @{ Nombre="GBPJPY"; Symbol="frxGBPJPY" },
            @{ Nombre="DJI";    Symbol="OTC_DJI"   },
            @{ Nombre="XAUUSD"; Symbol="frxXAUUSD" },
            @{ Nombre="USDCHF"; Symbol="frxUSDCHF" },
            @{ Nombre="GBPUSD"; Symbol="frxGBPUSD" }
        )
        Timeframes  = @("1h","4h")
        Duraciones  = @(1, 2)
        Confianzas  = @(60, 65)
    },
    @{
        Estrategia = "CCIReversal_v1"
        Etiqueta   = "Reversal con CCI (sin RSI)"
        Activos    = @(
            @{ Nombre="DJI";    Symbol="OTC_DJI"   },
            @{ Nombre="XAUUSD"; Symbol="frxXAUUSD" }
        )
        Timeframes  = @("1h","4h")
        Duraciones  = @(1, 2)
        Confianzas  = @(60, 65)
    }
)

# Calcular total de combos
$totalCombos = 0
foreach ($g in $GRIDS) {
    $totalCombos += $g.Activos.Count * $g.Timeframes.Count * $g.Duraciones.Count * $g.Confianzas.Count
}

$inicio = Get-Date
$combo  = 0

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  BACKTEST NUEVAS ESTRATEGIAS - ACTIVOS PROBLEMATICOS" -ForegroundColor Yellow
Write-Host ("  Total combos: {0}" -f $totalCombos) -ForegroundColor Cyan
Write-Host ("  Inicio      : {0}" -f $inicio.ToString("HH:mm:ss")) -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$RESULTADOS = [System.Collections.Generic.List[hashtable]]::new()

foreach ($grid in $GRIDS) {
    Write-Host ""
    Write-Host ("  === {0} ===" -f $grid.Etiqueta) -ForegroundColor Magenta
    Write-Host ("  Estrategia: {0}" -f $grid.Estrategia) -ForegroundColor DarkMagenta
    Write-Host ""

    foreach ($activo in $grid.Activos) {
        foreach ($tf in $grid.Timeframes) {
            foreach ($dur in $grid.Duraciones) {
                foreach ($conf in $grid.Confianzas) {
                    $combo++
                    $pct = [math]::Round($combo * 100.0 / $totalCombos)

                    $url  = "$Servidor/api/dinamicas/backtest"
                    $url += "?nombre=$([Uri]::EscapeDataString($grid.Estrategia))"
                    $url += "&symbol=$($activo.Symbol)"
                    $url += "&timeframe=$tf"
                    $url += "&duracion_velas=$dur"
                    $url += "&payout=$Payout"
                    $url += "&min_confianza=$conf"
                    $url += "&monto=$Monto"
                    $url += "&ventana=$Ventana"

                    Write-Host ("[{0,2}/{1}] {2,-7} {3,-3} | dur={4} | conf={5}  ({6}%)" -f `
                        $combo, $totalCombos, $activo.Nombre, $tf, $dur, $conf, $pct) -NoNewline

                    $sw = [System.Diagnostics.Stopwatch]::StartNew()
                    try {
                        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 300
                        $sw.Stop()
                        $json = $resp.Content | ConvertFrom-Json

                        if ($json.ok -eq $true) {
                            $wr   = [math]::Round($json.win_rate * 100, 2)
                            $pf   = [math]::Round($json.profit_factor, 3)
                            $pnl  = [math]::Round($json.profit_loss, 2)
                            $ops  = [int]$json.total_ops
                            $dd   = [math]::Round($json.max_drawdown, 2)
                            $secs = [math]::Round($sw.Elapsed.TotalSeconds, 1)

                            $wrColor = if ($wr -ge 54.05) { "Green" } else { "Red" }
                            Write-Host ("  => {0,4} ops  WR={1,6}%  PnL={2,8}  PF={3}  {4}s" -f `
                                $ops, $wr, ("+{0:N2}" -f $pnl), $pf, $secs) -ForegroundColor $wrColor

                            $RESULTADOS.Add(@{
                                Estrategia = $grid.Estrategia
                                Activo     = $activo.Nombre
                                Symbol     = $activo.Symbol
                                Timeframe  = $tf
                                Dur        = $dur
                                Conf       = $conf
                                Ops        = $ops
                                WinRate    = $wr
                                PnL        = $pnl
                                ProfitF    = $pf
                                MaxDD      = $dd
                                Ok         = $true
                            })
                        } else {
                            Write-Host ("  [ERR] {0}" -f $json.error) -ForegroundColor Red
                            $RESULTADOS.Add(@{ Estrategia=$grid.Estrategia; Activo=$activo.Nombre
                                Timeframe=$tf; Dur=$dur; Conf=$conf; Ok=$false })
                        }
                    } catch {
                        $sw.Stop()
                        Write-Host ("  [EXC] {0}" -f $_.Exception.Message) -ForegroundColor Red
                        $RESULTADOS.Add(@{ Estrategia=$grid.Estrategia; Activo=$activo.Nombre
                            Timeframe=$tf; Dur=$dur; Conf=$conf; Ok=$false })
                    }
                }
            }
        }
    }
}

$durTotal = [math]::Round((New-TimeSpan -Start $inicio -End (Get-Date)).TotalMinutes, 1)

# ============================================================
# RESULTADOS POR ESTRATEGIA
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host ("  RESULTADOS COMPLETOS  ({0} min)" -f $durTotal) -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green

foreach ($grid in $GRIDS) {
    Write-Host ""
    Write-Host ("  --- {0} ---" -f $grid.Estrategia) -ForegroundColor Magenta
    $hdr = "  {0,-7} {1,-4} {2,-4} {3,-4} {4,5} {5,7} {6,9} {7,6}" -f `
        "Activo","TF","Dur","Conf","Ops","WR%","PnL","PF"
    Write-Host $hdr -ForegroundColor White
    Write-Host ("  " + "-" * 58) -ForegroundColor DarkGray

    $filas = @($RESULTADOS | Where-Object { $_.Ok -eq $true -and $_.Estrategia -eq $grid.Estrategia } |
               Sort-Object WinRate -Descending)

    foreach ($r in $filas) {
        $wrColor = if ($r.WinRate -ge 54.05) { "Green" } else { "Red" }
        $pnlStr  = if ($r.PnL -ge 0) { "+{0:N2}" -f $r.PnL } else { "{0:N2}" -f $r.PnL }
        Write-Host ("  {0,-7} {1,-4} {2,-4} {3,-4} {4,5} {5,6}% {6,9} {7,6}" -f `
            $r.Activo, $r.Timeframe, $r.Dur, $r.Conf, $r.Ops, $r.WinRate, $pnlStr, $r.ProfitF) `
            -ForegroundColor $wrColor
    }
}

# ============================================================
# TOP 15 GLOBAL
# ============================================================
Write-Host ""
Write-Host "  TOP 15 GLOBAL (min 10 ops, por WR):" -ForegroundColor Cyan
$top = @($RESULTADOS | Where-Object { $_.Ok -eq $true -and $_.Ops -ge 10 } |
         Sort-Object WinRate -Descending | Select-Object -First 15)

$hdrT = "  {0,-22} {1,-8} {2,-4} {3,-4} {4,-4} {5,5} {6,7} {7,9} {8,6}" -f `
    "Estrategia","Activo","TF","Dur","Conf","Ops","WR%","PnL","PF"
Write-Host $hdrT -ForegroundColor White
Write-Host ("  " + "-" * 75) -ForegroundColor DarkGray

$rank = 0
foreach ($r in $top) {
    $rank++
    $wrColor = if ($r.WinRate -ge 54.05) { "Green" } else { "Red" }
    $pnlStr  = if ($r.PnL -ge 0) { "+{0:N2}" -f $r.PnL } else { "{0:N2}" -f $r.PnL }
    Write-Host ("{0,3}. {1,-22} {2,-8} {3,-4} {4,-4} {5,-4} {6,5} {7,6}% {8,9} {9,6}" -f `
        $rank, $r.Estrategia, $r.Activo, $r.Timeframe, $r.Dur, $r.Conf,
        $r.Ops, $r.WinRate, $pnlStr, $r.ProfitF) -ForegroundColor $wrColor
}

Write-Host ""
Write-Host ("  Total evaluados: {0} | Exitosos: {1} | Con min.10 ops: {2}" -f `
    $RESULTADOS.Count,
    ($RESULTADOS | Where-Object { $_.Ok }).Count,
    ($RESULTADOS | Where-Object { $_.Ok -and $_.Ops -ge 10 }).Count) -ForegroundColor DarkGray
Write-Host ""
