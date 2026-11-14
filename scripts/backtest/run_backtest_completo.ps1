# ============================================================
# run_backtest_completo.ps1  v1
#
# Ejecuta BollingerRsiReversal_v2 en todos los activos
# y calcula rendimiento diario, semanal y mensual.
#
# Uso:
#   .\run_backtest_completo.ps1
#   .\run_backtest_completo.ps1 -Estrategia EmaRsiCruce_v3 -Timeframe 4h
#   .\run_backtest_completo.ps1 -SoloResumen
# ============================================================
param(
    [string]  $Estrategia  = "BollingerRsiReversal_v2",
    [string]  $Timeframe   = "1h",
    [int]     $Duracion    = 1,
    [decimal] $Payout      = 85,
    [int]     $Confianza   = 65,
    [decimal] $Monto       = 10,
    [int]     $Ventana     = 100,
    [string]  $Servidor    = "http://localhost:8765",
    [switch]  $SoloResumen
)
# Token de las APIs locales 8765/8766 (ver agent_auth.ps1)
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')


$ErrorActionPreference = "Continue"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

# ============================================================
# CATALOGO DE ACTIVOS (symbol = nombre exacto en BD)
# ============================================================
$ACTIVOS = @(
    @{ Nombre="EURUSD"; Symbol="frxEURUSD"; Tipo="Forex";     Color="Cyan"    },
    @{ Nombre="GBPUSD"; Symbol="frxGBPUSD"; Tipo="Forex";     Color="Cyan"    },
    @{ Nombre="USDCHF"; Symbol="frxUSDCHF"; Tipo="Forex";     Color="Cyan"    },
    @{ Nombre="USDJPY"; Symbol="frxUSDJPY"; Tipo="Forex";     Color="Cyan"    },
    @{ Nombre="GBPJPY"; Symbol="frxGBPJPY"; Tipo="Forex";     Color="Cyan"    },
    @{ Nombre="XAUUSD"; Symbol="frxXAUUSD"; Tipo="Metal";     Color="Yellow"  },
    @{ Nombre="XAGUSD"; Symbol="frxXAGUSD"; Tipo="Metal";     Color="Yellow"  },
    @{ Nombre="OIL_US"; Symbol="frxUSOIL";  Tipo="Commodity"; Color="Yellow"  },
    @{ Nombre="NAS100"; Symbol="OTC_NDX";   Tipo="Index";     Color="Magenta" },
    @{ Nombre="SP500";  Symbol="OTC_SPC";   Tipo="Index";     Color="Magenta" },
    @{ Nombre="DJ30";   Symbol="OTC_DJI";   Tipo="Index";     Color="Magenta" },
    @{ Nombre="R_100";  Symbol="R_100";     Tipo="Synth";     Color="Green"   }
)

# ============================================================
# FUNCION: Calcular estadisticas por periodo (D/W/M)
# ============================================================
function Get-PeriodStats {
    param(
        [array]  $Ops,
        [string] $Periodo   # "D"=dia, "W"=semana, "M"=mes
    )

    if (-not $Ops -or $Ops.Count -eq 0) { return $null }

    $groups = @{}
    $ci     = [System.Globalization.CultureInfo]::InvariantCulture
    $cal    = $ci.Calendar
    $rule   = [System.Globalization.CalendarWeekRule]::FirstFourDayWeek
    $monday = [System.DayOfWeek]::Monday

    foreach ($op in $Ops) {
        try {
            # Las operaciones usan 'epoch' (Unix timestamp), no fecha_entrada
            $epochVal = $op.epoch
            if (-not $epochVal) { continue }
            $dt  = [DateTimeOffset]::FromUnixTimeSeconds([long]$epochVal).LocalDateTime
            $key = switch ($Periodo) {
                "D" { $dt.ToString("yyyy-MM-dd") }
                "W" {
                    $wn = $cal.GetWeekOfYear($dt, $rule, $monday)
                    ("{0}-W{1:D2}" -f $dt.Year, $wn)
                }
                "M" { $dt.ToString("yyyy-MM") }
            }
            if (-not $groups.ContainsKey($key)) { $groups[$key] = 0.0 }
            $groups[$key] += [double]$op.pnl
        } catch { }
    }

    if ($groups.Count -eq 0) { return $null }

    $vals      = @($groups.Values)
    $rentables = ($vals | Where-Object { $_ -gt 0 } | Measure-Object).Count
    $total     = $vals.Count
    $suma      = ($vals | Measure-Object -Sum).Sum
    $mejor     = ($vals | Measure-Object -Maximum).Maximum
    $peor      = ($vals | Measure-Object -Minimum).Minimum

    return @{
        Periodos     = $total
        PctRentables = [math]::Round($rentables * 100.0 / $total, 1)
        PromPnl      = [math]::Round($suma / $total, 2)
        MejorPnl     = [math]::Round($mejor, 2)
        PeorPnl      = [math]::Round($peor, 2)
        TotalPnl     = [math]::Round($suma, 2)
    }
}

# ============================================================
# FUNCION: Obtener operaciones de una sesion
# ============================================================
function Get-Operaciones {
    param([int] $SesionId)
    try {
        $url  = "$Servidor/api/dinamicas/operaciones?sesion_id=$SesionId"
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 120
        $json = $resp.Content | ConvertFrom-Json
        if ($json.ok -eq $true) { return @($json.operaciones) }
    } catch {
        Write-Host ("    [WARN] No se pudieron obtener operaciones: " + $_.Exception.Message) -ForegroundColor DarkGray
    }
    return @()
}

# ============================================================
# FUNCION: Ejecutar backtest via API REST
# ============================================================
function Invoke-BacktestActivo {
    param($Activo)

    $url  = "$Servidor/api/dinamicas/backtest"
    $url += "?nombre=$([Uri]::EscapeDataString($Estrategia))"
    $url += "&symbol=$($Activo.Symbol)"
    $url += "&timeframe=$Timeframe"
    $url += "&duracion_velas=$Duracion"
    $url += "&payout=$Payout"
    $url += "&min_confianza=$Confianza"
    $url += "&monto=$Monto"
    $url += "&ventana=$Ventana"
    # Sin desde_epoch ni hasta_epoch -> usa toda la data disponible

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 900
        $sw.Stop()
        $json = $resp.Content | ConvertFrom-Json
        if ($json.ok -eq $true) {
            $json | Add-Member -NotePropertyName "elapsed_s" `
                               -NotePropertyValue ([math]::Round($sw.Elapsed.TotalSeconds, 1)) `
                               -Force
            return $json
        }
        Write-Host ("    [ERROR servidor] " + $json.error) -ForegroundColor Red
    } catch {
        $sw.Stop()
        Write-Host ("    [EXCEPCION] " + $_.Exception.Message) -ForegroundColor Red
    }
    return $null
}

# ============================================================
# FUNCION: Mostrar bloque de un periodo
# ============================================================
function Show-PeriodBlock {
    param($Stats, [string] $Label)
    if (-not $Stats) { return }
    $pctColor = if ($Stats.PctRentables -ge 50) { "Green" } else { "Red" }
    $avgColor = if ($Stats.PromPnl -ge 0) { "Green" } else { "Red" }
    Write-Host ("  |   [{0}]" -f $Label) -ForegroundColor White
    Write-Host ("  |     Periodos con ops  : {0}" -f $Stats.Periodos)
    Write-Host ("  |     Periodos rentables: {0}%" -f $Stats.PctRentables) -ForegroundColor $pctColor
    Write-Host ("  |     Promedio PnL      : `${0}" -f $Stats.PromPnl) -ForegroundColor $avgColor
    Write-Host ("  |     Mejor periodo     : +`${0}" -f $Stats.MejorPnl) -ForegroundColor Green
    Write-Host ("  |     Peor periodo      :  `${0}" -f $Stats.PeorPnl) -ForegroundColor Red
    Write-Host ("  |     Total acumulado   : `${0}" -f $Stats.TotalPnl) -ForegroundColor $avgColor
}

# ============================================================
# CABECERA PRINCIPAL
# ============================================================
$fechaInicio = Get-Date
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  BACKTEST MASIVO" -ForegroundColor Yellow
Write-Host ("  Estrategia : {0}" -f $Estrategia) -ForegroundColor Cyan
Write-Host ("  Timeframe  : {0}   Duracion: {1} vela(s)" -f $Timeframe, $Duracion) -ForegroundColor Cyan
Write-Host ("  Payout     : {0}%   Confianza min: {1}%   Monto: `${2}" -f $Payout, $Confianza, $Monto) -ForegroundColor Cyan
Write-Host ("  Activos    : {0}   Sin limite de fechas" -f $ACTIVOS.Count) -ForegroundColor Cyan
Write-Host ("  Inicio     : {0}" -f $fechaInicio.ToString("dd/MM/yyyy HH:mm:ss")) -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# ============================================================
# LOOP PRINCIPAL
# ============================================================
$RESULTADOS = [System.Collections.Generic.List[hashtable]]::new()
$idx = 0

foreach ($activo in $ACTIVOS) {
    $idx++
    $progPct = [math]::Round($idx * 100.0 / $ACTIVOS.Count)
    Write-Host ("  [{0}/{1}] {2,-7} | {3}  ({4}%)" -f `
        $idx, $ACTIVOS.Count, $activo.Nombre, $activo.Symbol, $progPct) `
        -ForegroundColor $activo.Color

    $res = Invoke-BacktestActivo -Activo $activo

    if (-not $res) {
        $RESULTADOS.Add(@{
            Nombre = $activo.Nombre; Symbol = $activo.Symbol
            Tipo   = $activo.Tipo;  Ok     = $false
        })
        Write-Host "         Sin resultado" -ForegroundColor DarkGray
        Write-Host ""
        continue
    }

    $wr  = [math]::Round($res.win_rate * 100, 2)
    $pf  = [math]::Round($res.profit_factor, 3)
    $pnl = [math]::Round($res.profit_loss, 2)
    $dd  = [math]::Round($res.max_drawdown, 2)
    $ops = [int]$res.total_ops
    $gan = [int]$res.ganadas
    $per = [int]$res.perdidas

    if (-not $SoloResumen) {
        Write-Host ""
        $sepLine = "-" * 56
        Write-Host ("  +-- {0} | {1} | {2} --+" -f $Estrategia, $activo.Nombre, $Timeframe) `
            -ForegroundColor $activo.Color
        Write-Host ("  | Sesion        : #{0}" -f $res.sesion_id)
        Write-Host ("  | Operaciones   : {0:N0}  (Gan: {1:N0}  Per: {2:N0})" -f $ops, $gan, $per)

        $wrColor  = if ($wr  -ge 54.05) { "Green" } else { "Red" }
        $pnlColor = if ($pnl -ge 0) { "Green" } else { "Red" }
        $pfColor  = if ($pf  -ge 1)  { "Green" } else { "Red" }

        Write-Host ("  | Win Rate      : {0}%" -f $wr) -ForegroundColor $wrColor
        Write-Host ("  | Profit/Loss   : `${0:N2}" -f $pnl) -ForegroundColor $pnlColor
        Write-Host ("  | Profit Factor : {0}" -f $pf) -ForegroundColor $pfColor
        Write-Host ("  | Max Drawdown  : {0}%" -f $dd) -ForegroundColor Yellow
        Write-Host ("  | Tiempo calc   : {0}s" -f $res.elapsed_s) -ForegroundColor DarkGray
    }

    # -- Breakdown diario/semanal/mensual --
    $statsDia = $null
    $statsSem = $null
    $statsMes = $null

    if ($ops -gt 0) {
        Write-Host "         Obteniendo operaciones para breakdown..." -ForegroundColor DarkGray
        $ops_lista = Get-Operaciones -SesionId ([int]$res.sesion_id)

        if ($ops_lista.Count -gt 0) {
            $statsDia = Get-PeriodStats -Ops $ops_lista -Periodo "D"
            $statsSem = Get-PeriodStats -Ops $ops_lista -Periodo "W"
            $statsMes = Get-PeriodStats -Ops $ops_lista -Periodo "M"

            if (-not $SoloResumen) {
                Write-Host "  |"
                Write-Host "  | RENDIMIENTO POR PERIODO:" -ForegroundColor White
                Show-PeriodBlock -Stats $statsDia -Label "DIARIO"
                Write-Host "  |"
                Show-PeriodBlock -Stats $statsSem -Label "SEMANAL"
                Write-Host "  |"
                Show-PeriodBlock -Stats $statsMes -Label "MENSUAL"
            }
        } else {
            if (-not $SoloResumen) {
                Write-Host "  | (no se pudieron recuperar operaciones individuales)" -ForegroundColor DarkGray
            }
        }
    }

    if (-not $SoloResumen) { Write-Host "" }

    $RESULTADOS.Add(@{
        Nombre   = $activo.Nombre
        Symbol   = $activo.Symbol
        Tipo     = $activo.Tipo
        Ok       = $true
        SesionId = [int]$res.sesion_id
        Ops      = $ops
        WinRate  = $wr
        PnL      = $pnl
        ProfitF  = $pf
        MaxDD    = $dd
        DiaPct   = if ($statsDia) { $statsDia.PctRentables } else { $null }
        DiaAvg   = if ($statsDia) { $statsDia.PromPnl }      else { $null }
        SemPct   = if ($statsSem) { $statsSem.PctRentables } else { $null }
        SemAvg   = if ($statsSem) { $statsSem.PromPnl }      else { $null }
        MesPct   = if ($statsMes) { $statsMes.PctRentables } else { $null }
        MesAvg   = if ($statsMes) { $statsMes.PromPnl }      else { $null }
        MesMejor = if ($statsMes) { $statsMes.MejorPnl }     else { $null }
        MesPeor  = if ($statsMes) { $statsMes.PeorPnl }      else { $null }
        Elapsed  = $res.elapsed_s
    })
}

# ============================================================
# TABLA RESUMEN FINAL
# ============================================================
$durTotal = [math]::Round((New-TimeSpan -Start $fechaInicio -End (Get-Date)).TotalMinutes, 1)

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host ("  RESUMEN FINAL: {0} / {1}" -f $Estrategia, $Timeframe) -ForegroundColor Green
Write-Host ("  Duracion total: {0} min" -f $durTotal) -ForegroundColor DarkGray
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""

# Encabezado tabla
$hdr = "{0,-8} {1,-10} {2,5} {3,7} {4,9} {5,6} {6,7} {7,6} {8,6} {9,6} {10,8}" -f `
    "Activo","Tipo","Ops","WR%","PnL$","PF","MaxDD%","Dia%","Sem%","Mes%","Mes$/avg"
Write-Host $hdr -ForegroundColor White
Write-Host ("-" * 82) -ForegroundColor DarkGray

$exitosos = @($RESULTADOS | Where-Object { $_.Ok -eq $true })
$sorted   = $exitosos | Sort-Object -Property WinRate -Descending

foreach ($r in $sorted) {
    $wrColor = if ($r.WinRate -ge 54.05) { "Green" } else { "Red" }
    $pnlStr  = if ($r.PnL -ge 0) { ("+{0:N2}" -f $r.PnL) } else { ("{0:N2}" -f $r.PnL) }
    $diaPct  = if ($null -ne $r.DiaPct) { ("{0}%" -f $r.DiaPct) } else { "n/d" }
    $semPct  = if ($null -ne $r.SemPct) { ("{0}%" -f $r.SemPct) } else { "n/d" }
    $mesPct  = if ($null -ne $r.MesPct) { ("{0}%" -f $r.MesPct) } else { "n/d" }
    $mesAvg  = if ($null -ne $r.MesAvg) { ("`${0:N2}" -f $r.MesAvg) } else { "n/d" }

    $line = "{0,-8} {1,-10} {2,5} {3,6}% {4,9} {5,6} {6,6}% {7,6} {8,6} {9,6} {10,8}" -f `
        $r.Nombre, $r.Tipo, $r.Ops,
        $r.WinRate, $pnlStr,
        $r.ProfitF, $r.MaxDD,
        $diaPct, $semPct, $mesPct, $mesAvg
    Write-Host $line -ForegroundColor $wrColor
}

$fallidos = @($RESULTADOS | Where-Object { $_.Ok -eq $false })
if ($fallidos.Count -gt 0) {
    Write-Host ""
    Write-Host "  Sin resultado:" -ForegroundColor DarkGray
    foreach ($r in $fallidos) {
        Write-Host ("    {0} ({1})" -f $r.Nombre, $r.Symbol) -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "  Leyenda:" -ForegroundColor DarkGray
Write-Host "    WR%      = Win Rate (verde >= 54.05% = punto de equilibrio con payout 85%)" -ForegroundColor DarkGray
Write-Host "    PF       = Profit Factor (>1 = rentable)" -ForegroundColor DarkGray
Write-Host "    MaxDD%   = Maximo Drawdown" -ForegroundColor DarkGray
Write-Host "    Dia%/Sem%/Mes% = Porcentaje de periodos con ganancia neta" -ForegroundColor DarkGray
Write-Host "    Mes$/avg = PnL promedio mensual" -ForegroundColor DarkGray
Write-Host ""

$totalOps = ($exitosos | ForEach-Object { $_.Ops } | Measure-Object -Sum).Sum
$mejorWR  = ($exitosos | Sort-Object WinRate -Descending | Select-Object -First 1)
$mejorPnl = ($exitosos | Sort-Object PnL     -Descending | Select-Object -First 1)

Write-Host "  ESTADISTICAS GLOBALES:" -ForegroundColor Cyan
Write-Host ("    Activos probados  : {0}  (exitosos: {1}  fallidos: {2})" -f `
    $RESULTADOS.Count, $exitosos.Count, $fallidos.Count)
Write-Host ("    Total operaciones : {0:N0}" -f $totalOps)
if ($mejorWR) {
    Write-Host ("    Mejor Win Rate    : {0}% -> {1} ({2})" -f `
        $mejorWR.WinRate, $mejorWR.Nombre, $mejorWR.Symbol) -ForegroundColor Green
}
if ($mejorPnl) {
    Write-Host ("    Mayor ganancia    : `${0:N2} -> {1} ({2})" -f `
        $mejorPnl.PnL, $mejorPnl.Nombre, $mejorPnl.Symbol) -ForegroundColor Green
}
Write-Host ""
