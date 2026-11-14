# ============================================================
# run_optimizar_bb4h.ps1
# Grid search de BollingerRsiReversal en 4h para los 3
# mejores activos: NAS100, SP500, OIL_US.
#
# Variables:
#   Desviacion  : 2.0 / 2.5 / 3.0   (3 estrategias JSON)
#   Duracion    : 1 / 2 / 3 velas    (URL param)
#   Confianza   : 65 / 70 / 75       (URL param)
#   => 27 combos x 3 activos = 81 backtests
#
# Uso:
#   .\run_optimizar_bb4h.ps1
# ============================================================
param(
    [string] $Servidor = "http://localhost:8765",
    [decimal] $Payout  = 85,
    [decimal] $Monto   = 10,
    [int]    $Ventana  = 100
)
# Token de las APIs locales 8765/8766 (ver agent_auth.ps1)
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')


$ErrorActionPreference = "Continue"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

# ---- GRID DE PARAMETROS ----
$ACTIVOS = @(
    @{ Nombre="NAS100"; Symbol="OTC_NDX";  Tipo="Index"     },
    @{ Nombre="SP500";  Symbol="OTC_SPC";  Tipo="Index"     },
    @{ Nombre="OIL_US"; Symbol="frxUSOIL"; Tipo="Commodity" }
)
$ESTRATEGIAS = @(
    @{ Nombre="BollingerRsiReversal_d20"; Desv=2.0 },
    @{ Nombre="BollingerRsiReversal_v2";  Desv=2.5 },
    @{ Nombre="BollingerRsiReversal_d30"; Desv=3.0 }
)
$DURACIONES  = @(1, 2, 3)
$CONFIANZAS  = @(65, 70, 75)

$totalCombos = $ACTIVOS.Count * $ESTRATEGIAS.Count * $DURACIONES.Count * $CONFIANZAS.Count
$inicio      = Get-Date

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  OPTIMIZACION BollingerRsiReversal / 4h" -ForegroundColor Yellow
Write-Host ("  Activos    : {0}   Desviaciones: {1}   Duraciones: {2}   Confianzas: {3}" -f `
    $ACTIVOS.Count, $ESTRATEGIAS.Count, $DURACIONES.Count, $CONFIANZAS.Count) -ForegroundColor Cyan
Write-Host ("  Total combos: {0}" -f $totalCombos) -ForegroundColor Cyan
Write-Host ("  Inicio     : {0}" -f $inicio.ToString("HH:mm:ss")) -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# ---- ALMACENAMIENTO DE RESULTADOS ----
$RESULTADOS = [System.Collections.Generic.List[hashtable]]::new()
$combo = 0

foreach ($activo in $ACTIVOS) {
    foreach ($est in $ESTRATEGIAS) {
        foreach ($dur in $DURACIONES) {
            foreach ($conf in $CONFIANZAS) {
                $combo++
                $pct = [math]::Round($combo * 100.0 / $totalCombos)

                $url  = "$Servidor/api/dinamicas/backtest"
                $url += "?nombre=$([Uri]::EscapeDataString($est.Nombre))"
                $url += "&symbol=$($activo.Symbol)"
                $url += "&timeframe=4h"
                $url += "&duracion_velas=$dur"
                $url += "&payout=$Payout"
                $url += "&min_confianza=$conf"
                $url += "&monto=$Monto"
                $url += "&ventana=$Ventana"

                Write-Host ("[{0,2}/{1}] {2,-7} | d={3} | dur={4} | conf={5}  ({6}%)" -f `
                    $combo, $totalCombos, $activo.Nombre, $est.Desv, $dur, $conf, $pct) -NoNewline

                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                try {
                    $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 300
                    $sw.Stop()
                    $json = $resp.Content | ConvertFrom-Json

                    if ($json.ok -eq $true) {
                        $wr  = [math]::Round($json.win_rate * 100, 2)
                        $pf  = [math]::Round($json.profit_factor, 3)
                        $pnl = [math]::Round($json.profit_loss, 2)
                        $ops = [int]$json.total_ops
                        $dd  = [math]::Round($json.max_drawdown, 2)
                        $secs = [math]::Round($sw.Elapsed.TotalSeconds, 1)

                        $wrColor = if ($wr -ge 54.05) { "Green" } else { "Red" }
                        Write-Host ("  => {0,4} ops  WR={1,6}%  PnL={2,8}  PF={3}  {4}s" -f `
                            $ops, $wr, ("+{0:N2}" -f $pnl), $pf, $secs) -ForegroundColor $wrColor

                        $RESULTADOS.Add(@{
                            Activo    = $activo.Nombre
                            Symbol    = $activo.Symbol
                            Estrategia= $est.Nombre
                            Desv      = $est.Desv
                            Dur       = $dur
                            Conf      = $conf
                            Ops       = $ops
                            WinRate   = $wr
                            PnL       = $pnl
                            ProfitF   = $pf
                            MaxDD     = $dd
                            SesionId  = [int]$json.sesion_id
                            Ok        = $true
                        })
                    } else {
                        Write-Host ("  [ERR] {0}" -f $json.error) -ForegroundColor Red
                        $RESULTADOS.Add(@{
                            Activo=$activo.Nombre; Estrategia=$est.Nombre
                            Desv=$est.Desv; Dur=$dur; Conf=$conf; Ok=$false
                        })
                    }
                } catch {
                    $sw.Stop()
                    Write-Host ("  [EXC] {0}" -f $_.Exception.Message) -ForegroundColor Red
                    $RESULTADOS.Add(@{
                        Activo=$activo.Nombre; Estrategia=$est.Nombre
                        Desv=$est.Desv; Dur=$dur; Conf=$conf; Ok=$false
                    })
                }
            }
        }
    }
}

$durTotal = [math]::Round((New-TimeSpan -Start $inicio -End (Get-Date)).TotalMinutes, 1)

# ============================================================
# TABLA PIVOT POR ACTIVO
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host ("  RESULTADOS COMPLETOS  ({0} min)" -f $durTotal) -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""

foreach ($act in @("NAS100","SP500","OIL_US")) {
    Write-Host ("  --- {0} ---" -f $act) -ForegroundColor Yellow
    $hdr = "{0,-6} {1,-4} {2,-4} {3,5} {4,7} {5,9} {6,6} {7,7}" -f `
        "Desv","Dur","Conf","Ops","WR%","PnL$","PF","MaxDD%"
    Write-Host ("  " + $hdr) -ForegroundColor White
    Write-Host ("  " + "-" * 56) -ForegroundColor DarkGray

    $filas = @($RESULTADOS | Where-Object { $_.Ok -eq $true -and $_.Activo -eq $act } |
               Sort-Object WinRate -Descending)
    foreach ($r in $filas) {
        $wrColor = if ($r.WinRate -ge 54.05) { "Green" } else { "Red" }
        $pnlStr  = if ($r.PnL -ge 0) { "+{0:N2}" -f $r.PnL } else { "{0:N2}" -f $r.PnL }
        $line = "  {0,-6} {1,-4} {2,-4} {3,5} {4,6}% {5,9} {6,6} {7,6}%" -f `
            $r.Desv, $r.Dur, $r.Conf, $r.Ops, $r.WinRate, $pnlStr, $r.ProfitF, $r.MaxDD
        Write-Host $line -ForegroundColor $wrColor
    }
    Write-Host ""
}

# ============================================================
# TOP 10 GLOBAL
# ============================================================
Write-Host "  TOP 10 COMBINACIONES (por WR, minimo 10 ops):" -ForegroundColor Cyan
$top = @($RESULTADOS | Where-Object { $_.Ok -eq $true -and $_.Ops -ge 10 } |
         Sort-Object WinRate -Descending | Select-Object -First 10)

$hdrT = "{0,-8} {1,-6} {2,-4} {3,-4} {4,-4} {5,5} {6,7} {7,9} {8,6}" -f `
    "Activo","Desv","Dur","Conf","Ops","","WR%","PnL$","PF"
Write-Host ("  " + $hdrT) -ForegroundColor White
Write-Host ("  " + "-" * 62) -ForegroundColor DarkGray

$rank = 0
foreach ($r in $top) {
    $rank++
    $wrColor = if ($r.WinRate -ge 54.05) { "Green" } else { "Red" }
    $pnlStr  = if ($r.PnL -ge 0) { "+{0:N2}" -f $r.PnL } else { "{0:N2}" -f $r.PnL }
    $line = "  {0,-8} {1,-6} {2,-4} {3,-4} {4,5}   {5,6}% {6,9} {7,6}" -f `
        $r.Activo, $r.Desv, $r.Dur, $r.Conf, $r.Ops, $r.WinRate, $pnlStr, $r.ProfitF
    Write-Host ("{0,3}. {1}" -f $rank, $line.TrimStart()) -ForegroundColor $wrColor
}

Write-Host ""
Write-Host ("  Total combinaciones evaluadas: {0}" -f $RESULTADOS.Count) -ForegroundColor DarkGray
Write-Host ""
