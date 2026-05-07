# ============================================================
# reporte_rentabilidad_activos.ps1
# Reporte de rentabilidad por activo considerando TODAS las
# estrategias y combinaciones ejecutadas en backtest.
#
# Clasifica activos en:
#   NUNCA_RENTABLE     : ninguna combinacion supera 54.05% WR
#   RARAMENTE_RENTABLE : 1 o 2 combos superan el umbral
#   RENTABLE           : 3 o mas combos superan el umbral
#
# Uso:
#   .\reporte_rentabilidad_activos.ps1
#   .\reporte_rentabilidad_activos.ps1 -MinOps 20 -Detalle
# ============================================================
param(
    [int]    $MinOps    = 10,
    [decimal]$Payout    = 85,
    [switch] $Detalle
)

$ErrorActionPreference = "Continue"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$CONTAINER = "traiding_postgres"
$PGUSER    = "traiding_user"
$PGDB      = "traiding_db"
$BREAK_EVEN = [math]::Round(100.0 / (100 + $Payout) * 100, 2)   # 54.05% para payout=85

function Invoke-SQL {
    param([string]$Query)
    $lines = docker exec -i $CONTAINER psql -U $PGUSER -d $PGDB -t -A -F"|" -c $Query 2>&1
    return $lines | Where-Object { $_ -and $_ -notmatch "^\s*$" }
}

# ---- VERIFICAR CONEXION ----
$ping = Invoke-SQL "SELECT 1;"
if (-not ($ping -match "1")) {
    Write-Host "[ERROR] No se pudo conectar a PostgreSQL en contenedor '$CONTAINER'" -ForegroundColor Red
    exit 1
}

$ahora = Get-Date

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  REPORTE DE RENTABILIDAD POR ACTIVO" -ForegroundColor Yellow
Write-Host ("  Generado : {0}" -f $ahora.ToString("yyyy-MM-dd HH:mm:ss")) -ForegroundColor Cyan
Write-Host ("  Umbral   : WR >= {0}%  (Payout {1}%)" -f $BREAK_EVEN, $Payout) -ForegroundColor Cyan
Write-Host ("  Min. ops : {0}" -f $MinOps) -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# ============================================================
# QUERY 1 - RESUMEN GLOBAL POR ACTIVO
# ============================================================
$queryResumen = @"
WITH sesiones_validas AS (
    SELECT symbol, estrategia, timeframe, duracion_velas, min_confianza,
           win_rate, profit_loss_total, profit_factor, max_drawdown, total_operaciones
    FROM public.backtest_sesiones
    WHERE total_operaciones >= $MinOps
),
resumen AS (
    SELECT
        symbol,
        COUNT(*)                                                   AS total_combos,
        COUNT(DISTINCT estrategia)                                 AS estrategias,
        ROUND(MAX(win_rate)*100,2)                                 AS mejor_wr,
        ROUND(MAX(profit_loss_total),2)                            AS mejor_pnl,
        ROUND(MAX(profit_factor),3)                                AS mejor_pf,
        SUM(CASE WHEN win_rate >= $($BREAK_EVEN/100) THEN 1 ELSE 0 END) AS combos_rentables,
        SUM(total_operaciones)                                     AS total_ops
    FROM sesiones_validas
    GROUP BY symbol
)
SELECT symbol, total_combos, estrategias,
       mejor_wr, mejor_pnl, mejor_pf, combos_rentables, total_ops,
       CASE WHEN combos_rentables = 0 THEN 'NUNCA_RENTABLE'
            WHEN combos_rentables <= 2 THEN 'RARAMENTE_RENTABLE'
            ELSE 'RENTABLE'
       END AS estado
FROM resumen
ORDER BY mejor_wr ASC;
"@

$rowsResumen = Invoke-SQL $queryResumen

# ---- PARSEAR RESUMEN ----
$activos = [System.Collections.Generic.List[hashtable]]::new()
foreach ($row in $rowsResumen) {
    $c = $row -split "\|"
    if ($c.Count -lt 9) { continue }
    $activos.Add(@{
        Symbol    = $c[0]
        Combos    = [int]$c[1]
        Estrat    = [int]$c[2]
        MejorWR   = [decimal]$c[3]
        MejorPnL  = [decimal]$c[4]
        MejorPF   = [decimal]$c[5]
        Rentables = [int]$c[6]
        TotalOps  = [int]$c[7]
        Estado    = $c[8]
    })
}

# ============================================================
# QUERY 2 - MEJOR COMBINACION POR ACTIVO
# ============================================================
$queryMejor = @"
SELECT DISTINCT ON (symbol)
    symbol, estrategia, timeframe, duracion_velas, min_confianza,
    ROUND(win_rate*100,2), ROUND(profit_loss_total,2),
    ROUND(profit_factor,3), total_operaciones
FROM public.backtest_sesiones
WHERE total_operaciones >= $MinOps
ORDER BY symbol, win_rate DESC;
"@

$rowsMejor = Invoke-SQL $queryMejor
$mejorPorActivo = @{}
foreach ($row in $rowsMejor) {
    $c = $row -split "\|"
    if ($c.Count -lt 9) { continue }
    $mejorPorActivo[$c[0]] = @{
        Estrategia = $c[1]; Timeframe = $c[2]; DurVelas = $c[3]; Conf = $c[4]
        WR = [decimal]$c[5]; PnL = [decimal]$c[6]; PF = [decimal]$c[7]; Ops = [int]$c[8]
    }
}

# ============================================================
# QUERY 3 - RENTABILIDAD DIARIA / SEMANAL / MENSUAL
#           (sobre la mejor sesion por WR de cada activo)
# ============================================================
$queryPeriodos = @"
WITH mejor_sesion AS (
    SELECT DISTINCT ON (symbol) id, symbol
    FROM public.backtest_sesiones
    WHERE total_operaciones >= $MinOps
    ORDER BY symbol, win_rate DESC
),
ops AS (
    SELECT m.symbol, o.profit_loss,
           date_trunc('day',   to_timestamp(o.epoch_entrada)) AS dia,
           date_trunc('week',  to_timestamp(o.epoch_entrada)) AS semana,
           date_trunc('month', to_timestamp(o.epoch_entrada)) AS mes
    FROM mejor_sesion m
    JOIN public.backtest_operaciones o ON o.sesion_id = m.id
    WHERE o.epoch_entrada > 0
),
diario AS (
    SELECT symbol,
           ROUND(AVG(pnl_d)::numeric,2) AS avg_dia,
           ROUND(SUM(pnl_d)::numeric,2) AS tot_dia,
           ROUND(MAX(pnl_d)::numeric,2) AS max_dia,
           ROUND(MIN(pnl_d)::numeric,2) AS min_dia,
           COUNT(*) AS n_dias
    FROM (SELECT symbol, dia, SUM(profit_loss) AS pnl_d FROM ops GROUP BY symbol, dia) x
    GROUP BY symbol
),
semanal AS (
    SELECT symbol,
           ROUND(AVG(pnl_s)::numeric,2) AS avg_sem,
           ROUND(SUM(pnl_s)::numeric,2) AS tot_sem,
           ROUND(MAX(pnl_s)::numeric,2) AS max_sem,
           ROUND(MIN(pnl_s)::numeric,2) AS min_sem,
           COUNT(*) AS n_sem
    FROM (SELECT symbol, semana, SUM(profit_loss) AS pnl_s FROM ops GROUP BY symbol, semana) x
    GROUP BY symbol
),
mensual AS (
    SELECT symbol,
           ROUND(AVG(pnl_m)::numeric,2) AS avg_mes,
           ROUND(SUM(pnl_m)::numeric,2) AS tot_mes,
           ROUND(MAX(pnl_m)::numeric,2) AS max_mes,
           ROUND(MIN(pnl_m)::numeric,2) AS min_mes,
           COUNT(*) AS n_mes
    FROM (SELECT symbol, mes, SUM(profit_loss) AS pnl_m FROM ops GROUP BY symbol, mes) x
    GROUP BY symbol
)
SELECT d.symbol,
       d.avg_dia, d.tot_dia, d.max_dia, d.min_dia, d.n_dias,
       s.avg_sem, s.tot_sem, s.max_sem, s.min_sem, s.n_sem,
       m.avg_mes, m.tot_mes, m.max_mes, m.min_mes, m.n_mes
FROM diario d
JOIN semanal s ON s.symbol = d.symbol
JOIN mensual m ON m.symbol = d.symbol
ORDER BY d.avg_dia DESC;
"@

$rowsPeriodos = Invoke-SQL $queryPeriodos
$periodoPorActivo = @{}
foreach ($row in $rowsPeriodos) {
    $c = $row -split "\|"
    if ($c.Count -lt 16) { continue }
    $periodoPorActivo[$c[0]] = @{
        AvgDia = [decimal]$c[1];  TotDia = [decimal]$c[2]
        MaxDia = [decimal]$c[3];  MinDia = [decimal]$c[4];  NDias  = [int]$c[5]
        AvgSem = [decimal]$c[6];  TotSem = [decimal]$c[7]
        MaxSem = [decimal]$c[8];  MinSem = [decimal]$c[9];  NSem   = [int]$c[10]
        AvgMes = [decimal]$c[11]; TotMes = [decimal]$c[12]
        MaxMes = [decimal]$c[13]; MinMes = [decimal]$c[14]; NMeses = [int]$c[15]
    }
}

function Format-PnLPeriod {
    param([decimal]$Val)
    if ($Val -ge 0) { return "+`${0:N2}" -f $Val }
    else            { return "-`${0:N2}" -f [math]::Abs($Val) }
}

function Show-Periodo {
    param($sym, $col)
    $p = $periodoPorActivo[$sym]
    if (-not $p) { return }
    $avgD = Format-PnLPeriod $p.AvgDia;  $totD = Format-PnLPeriod $p.TotDia
    $maxD = Format-PnLPeriod $p.MaxDia;  $minD = Format-PnLPeriod $p.MinDia
    $avgS = Format-PnLPeriod $p.AvgSem;  $totS = Format-PnLPeriod $p.TotSem
    $maxS = Format-PnLPeriod $p.MaxSem;  $minS = Format-PnLPeriod $p.MinSem
    $avgM = Format-PnLPeriod $p.AvgMes;  $totM = Format-PnLPeriod $p.TotMes
    $maxM = Format-PnLPeriod $p.MaxMes;  $minM = Format-PnLPeriod $p.MinMes
    Write-Host ("    >> Dia  ({0,3}d): Avg={1,9}  Total={2,10}  Mejor={3,9}  Peor={4,9}" -f `
        $p.NDias, $avgD, $totD, $maxD, $minD) -ForegroundColor $col
    Write-Host ("    >> Sem  ({0,3}s): Avg={1,9}  Total={2,10}  Mejor={3,9}  Peor={4,9}" -f `
        $p.NSem, $avgS, $totS, $maxS, $minS) -ForegroundColor $col
    Write-Host ("    >> Mes  ({0,3}m): Avg={1,9}  Total={2,10}  Mejor={3,9}  Peor={4,9}" -f `
        $p.NMeses, $avgM, $totM, $maxM, $minM) -ForegroundColor $col
}

# ============================================================
# SECCION 1 - ACTIVOS NUNCA RENTABLES
# ============================================================
$nunca = @($activos | Where-Object { $_.Estado -eq "NUNCA_RENTABLE" })
Write-Host ""
Write-Host ("  [1/3] ACTIVOS NUNCA RENTABLES ({0} activos)" -f $nunca.Count) -ForegroundColor Red
Write-Host ("        Ninguna combinacion supero {0}% WR con min. {1} ops" -f $BREAK_EVEN, $MinOps) -ForegroundColor DarkGray
Write-Host ""

if ($nunca.Count -gt 0) {
    $hdr = "  {0,-14} {1,6} {2,5} {3,8} {4,9} {5,7} {6,10}" -f `
        "Activo","Combos","Estrat","MejorWR%","MejorPnL","MejorPF","TotalOps"
    Write-Host $hdr -ForegroundColor White
    Write-Host ("  " + "-" * 65) -ForegroundColor DarkGray
    foreach ($a in $nunca) {
        $m = $mejorPorActivo[$a.Symbol]
        $pnlStr = if ($a.MejorPnL -ge 0) { "+{0:N2}" -f $a.MejorPnL } else { "{0:N2}" -f $a.MejorPnL }
        Write-Host ("  {0,-14} {1,6} {2,5} {3,7}% {4,9} {5,7} {6,10}" -f `
            $a.Symbol, $a.Combos, $a.Estrat, $a.MejorWR, $pnlStr, $a.MejorPF, $a.TotalOps) -ForegroundColor Red
        if ($m) {
            Write-Host ("    >> Mejor: {0} / {1} / dur={2} / conf={3}  =>  WR={4}%  PnL={5}  PF={6}  ({7} ops)" -f `
                $m.Estrategia, $m.Timeframe, $m.DurVelas, $m.Conf, $m.WR, $m.PnL, $m.PF, $m.Ops) -ForegroundColor DarkRed
            Show-Periodo $a.Symbol "DarkRed"
        }
    }
}

# ============================================================
# SECCION 2 - ACTIVOS RARAMENTE RENTABLES
# ============================================================
$rara = @($activos | Where-Object { $_.Estado -eq "RARAMENTE_RENTABLE" })
Write-Host ""
Write-Host ("  [2/3] ACTIVOS RARAMENTE RENTABLES ({0} activos)" -f $rara.Count) -ForegroundColor Yellow
Write-Host "        Solo 1-2 combinaciones superan el umbral de rentabilidad" -ForegroundColor DarkGray
Write-Host ""

if ($rara.Count -gt 0) {
    $hdr = "  {0,-14} {1,6} {2,5} {3,8} {4,9} {5,7} {6,8} {7,10}" -f `
        "Activo","Combos","Estrat","MejorWR%","MejorPnL","MejorPF","Rentab.","TotalOps"
    Write-Host $hdr -ForegroundColor White
    Write-Host ("  " + "-" * 72) -ForegroundColor DarkGray
    foreach ($a in ($rara | Sort-Object MejorWR -Descending)) {
        $m = $mejorPorActivo[$a.Symbol]
        $pnlStr = if ($a.MejorPnL -ge 0) { "+{0:N2}" -f $a.MejorPnL } else { "{0:N2}" -f $a.MejorPnL }
        Write-Host ("  {0,-14} {1,6} {2,5} {3,7}% {4,9} {5,7} {6,8} {7,10}" -f `
            $a.Symbol, $a.Combos, $a.Estrat, $a.MejorWR, $pnlStr, $a.MejorPF, `
            ("{0}/total" -f $a.Rentables), $a.TotalOps) -ForegroundColor Yellow
        if ($m) {
            Write-Host ("    >> Mejor: {0} / {1} / dur={2} / conf={3}  =>  WR={4}%  PnL={5}  PF={6}  ({7} ops)" -f `
                $m.Estrategia, $m.Timeframe, $m.DurVelas, $m.Conf, $m.WR, $m.PnL, $m.PF, $m.Ops) -ForegroundColor DarkYellow
            Show-Periodo $a.Symbol "DarkYellow"
        }
    }
}

# ============================================================
# SECCION 3 - ACTIVOS RENTABLES
# ============================================================
$rent = @($activos | Where-Object { $_.Estado -eq "RENTABLE" })
Write-Host ""
Write-Host ("  [3/3] ACTIVOS RENTABLES ({0} activos)" -f $rent.Count) -ForegroundColor Green
Write-Host "        3 o mas combinaciones superan el umbral de rentabilidad" -ForegroundColor DarkGray
Write-Host ""

if ($rent.Count -gt 0) {
    $hdr = "  {0,-14} {1,6} {2,5} {3,8} {4,9} {5,7} {6,8} {7,10}" -f `
        "Activo","Combos","Estrat","MejorWR%","MejorPnL","MejorPF","Rentab.","TotalOps"
    Write-Host $hdr -ForegroundColor White
    Write-Host ("  " + "-" * 72) -ForegroundColor DarkGray
    foreach ($a in ($rent | Sort-Object MejorWR -Descending)) {
        $m = $mejorPorActivo[$a.Symbol]
        $pnlStr = if ($a.MejorPnL -ge 0) { "+{0:N2}" -f $a.MejorPnL } else { "{0:N2}" -f $a.MejorPnL }
        Write-Host ("  {0,-14} {1,6} {2,5} {3,7}% {4,9} {5,7} {6,8} {7,10}" -f `
            $a.Symbol, $a.Combos, $a.Estrat, $a.MejorWR, $pnlStr, $a.MejorPF, `
            ("{0}/total" -f $a.Rentables), $a.TotalOps) -ForegroundColor Green
        if ($m) {
            Write-Host ("    >> Mejor: {0} / {1} / dur={2} / conf={3}  =>  WR={4}%  PnL={5}  PF={6}  ({7} ops)" -f `
                $m.Estrategia, $m.Timeframe, $m.DurVelas, $m.Conf, $m.WR, $m.PnL, $m.PF, $m.Ops) -ForegroundColor DarkGreen
            Show-Periodo $a.Symbol "DarkGreen"
        }
    }
}

# ============================================================
# SECCION 4 - TABLA CONSOLIDADA DE RENTABILIDAD POR PERIODO
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  RENTABILIDAD PROYECTADA POR PERIODO (mejor sesion/activo)" -ForegroundColor Yellow
Write-Host "  Basado en el promedio historico de dias/semanas/meses" -ForegroundColor DarkGray
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

$hdrP1 = "  {0,-14} {1,-20} {2,9} {3,10} {4,9} {5,10} {6,9} {7,10} {8,10}" -f `
    "Activo","Estado","AvgDia","TotalDia","AvgSem","TotalSem","AvgMes","TotalMes","TotalOps"
$hdrP2 = "  {0,-14} {1,-20} {2,9} {3,10} {4,9} {5,10} {6,9} {7,10}" -f `
    "","","MejDia","PeorDia","MejSem","PeorSem","MejMes","PeorMes"
Write-Host $hdrP1 -ForegroundColor White
Write-Host $hdrP2 -ForegroundColor DarkGray
Write-Host ("  " + "-" * 107) -ForegroundColor DarkGray

# Separar rentables de no rentables
$activosRentables = @($activos | Where-Object { $_.Estado -ne "NUNCA_RENTABLE" } | Sort-Object {
    $p = $periodoPorActivo[$_.Symbol]; if ($p) { $p.AvgDia } else { -9999 }
} -Descending)

$activosNunca = @($activos | Where-Object { $_.Estado -eq "NUNCA_RENTABLE" } | Sort-Object {
    $p = $periodoPorActivo[$_.Symbol]; if ($p) { $p.AvgDia } else { -9999 }
} -Descending)

# Acumuladores para fila TOTAL (solo activos con rendimiento positivo o raramente)
$sumAvgDia = [decimal]0; $sumTotDia = [decimal]0
$sumAvgSem = [decimal]0; $sumTotSem = [decimal]0
$sumAvgMes = [decimal]0; $sumTotMes = [decimal]0
$sumOps    = 0

function Write-FilaPeriodo {
    param($a, $col)
    $p = $periodoPorActivo[$a.Symbol]
    if (-not $p) { return }
    $avgD = Format-PnLPeriod $p.AvgDia;  $totD = Format-PnLPeriod $p.TotDia
    $maxD = Format-PnLPeriod $p.MaxDia;  $minD = Format-PnLPeriod $p.MinDia
    $avgS = Format-PnLPeriod $p.AvgSem;  $totS = Format-PnLPeriod $p.TotSem
    $maxS = Format-PnLPeriod $p.MaxSem;  $minS = Format-PnLPeriod $p.MinSem
    $avgM = Format-PnLPeriod $p.AvgMes;  $totM = Format-PnLPeriod $p.TotMes
    $maxM = Format-PnLPeriod $p.MaxMes;  $minM = Format-PnLPeriod $p.MinMes
    Write-Host ("  {0,-14} {1,-20} {2,9} {3,10} {4,9} {5,10} {6,9} {7,10} {8,10}" -f `
        $a.Symbol, $a.Estado, $avgD, $totD, $avgS, $totS, $avgM, $totM, $a.TotalOps) -ForegroundColor $col
    Write-Host ("  {0,-14} {1,-20} {2,9} {3,10} {4,9} {5,10} {6,9} {7,10}" -f `
        "","", $maxD, $minD, $maxS, $minS, $maxM, $minM) -ForegroundColor DarkGray
    Write-Host ("  {0,-14} {1,-20} {2} dias / {3} sem / {4} meses" -f `
        "","", $p.NDias, $p.NSem, $p.NMeses) -ForegroundColor DarkGray
    Write-Host ("  " + "-" * 107) -ForegroundColor DarkGray
}

# ---- RENTABLE + RARAMENTE_RENTABLE ----
foreach ($a in $activosRentables) {
    $p = $periodoPorActivo[$a.Symbol]
    if (-not $p) { continue }
    $col = if ($a.Estado -eq "RENTABLE") { "Green" } else { "Yellow" }
    Write-FilaPeriodo $a $col
    $sumAvgDia += $p.AvgDia;  $sumTotDia += $p.TotDia
    $sumAvgSem += $p.AvgSem;  $sumTotSem += $p.TotSem
    $sumAvgMes += $p.AvgMes;  $sumTotMes += $p.TotMes
    $sumOps    += $a.TotalOps
}

# ---- FILA TOTAL (solo activos rentables) ----
$nAct  = $activosRentables.Count
$pAvgD = if ($nAct -gt 0) { [math]::Round($sumAvgDia / $nAct, 2) } else { 0 }
$pAvgS = if ($nAct -gt 0) { [math]::Round($sumAvgSem / $nAct, 2) } else { 0 }
$pAvgM = if ($nAct -gt 0) { [math]::Round($sumAvgMes / $nAct, 2) } else { 0 }

$tAvgD = Format-PnLPeriod $pAvgD;    $tTotD = Format-PnLPeriod $sumTotDia
$tAvgS = Format-PnLPeriod $pAvgS;    $tTotS = Format-PnLPeriod $sumTotSem
$tAvgM = Format-PnLPeriod $pAvgM;    $tTotM = Format-PnLPeriod $sumTotMes
Write-Host ("  {0,-14} {1,-20} {2,9} {3,10} {4,9} {5,10} {6,9} {7,10} {8,10}" -f `
    ">>> PROMEDIO", ("({0} activos)" -f $nAct),
    $tAvgD, $tTotD, $tAvgS, $tTotS, $tAvgM, $tTotM, $sumOps) -ForegroundColor Cyan
Write-Host ("  {0,-14} {1,-20} {2,-9} {3,-10} {4,-9} {5,-10} {6,-9} {7,-10}" -f `
    "","","(avg/act)","(total $)","(avg/act)","(total $)","(avg/act)","(total $)") -ForegroundColor DarkCyan
Write-Host ("  " + "=" * 107) -ForegroundColor Cyan

# ---- NUNCA RENTABLES (separados) ----
if ($activosNunca.Count -gt 0) {
    Write-Host ""
    Write-Host ("  --- EXCLUIDOS (NUNCA_RENTABLE: {0} activos) ---" -f $activosNunca.Count) -ForegroundColor DarkRed
    Write-Host $hdrP1 -ForegroundColor DarkGray
    Write-Host ("  " + "-" * 107) -ForegroundColor DarkGray
    foreach ($a in $activosNunca) {
        $p = $periodoPorActivo[$a.Symbol]
        if (-not $p) { continue }
        Write-FilaPeriodo $a "DarkRed"
    }
}

Write-Host ""
Write-Host "  Nota: proyeccion basada en frecuencia historica de senales." -ForegroundColor DarkGray
Write-Host "        No garantiza rendimiento futuro. Basado en operaciones reales del backtest." -ForegroundColor DarkGray

# ============================================================
# SECCION 5 - DETALLE POR ESTRATEGIA (opcional con -Detalle)
# ============================================================
if ($Detalle) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  DETALLE POR ACTIVO Y ESTRATEGIA" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan

    $queryDetalle = @"
SELECT symbol, estrategia,
       COUNT(*) AS combos,
       ROUND(MAX(win_rate)*100,2) AS mejor_wr,
       ROUND(MAX(profit_loss_total),2) AS mejor_pnl,
       ROUND(MAX(profit_factor),3) AS mejor_pf,
       SUM(CASE WHEN win_rate >= $($BREAK_EVEN/100) THEN 1 ELSE 0 END) AS ok_combos
FROM public.backtest_sesiones
WHERE total_operaciones >= $MinOps
GROUP BY symbol, estrategia
ORDER BY symbol, mejor_wr DESC;
"@

    $rowsDetalle = Invoke-SQL $queryDetalle
    $simboloActual = ""

    foreach ($row in $rowsDetalle) {
        $c = $row -split "\|"
        if ($c.Count -lt 7) { continue }
        $sym  = $c[0]; $est = $c[1]; $cmb = $c[2]
        $wr   = [decimal]$c[3]; $pnl = [decimal]$c[4]; $pf = [decimal]$c[5]; $ok = [int]$c[6]

        if ($sym -ne $simboloActual) {
            $simboloActual = $sym
            $estadoActivo = ($activos | Where-Object { $_.Symbol -eq $sym } | Select-Object -First 1).Estado
            $colSym = switch ($estadoActivo) {
                "NUNCA_RENTABLE"     { "Red"    }
                "RARAMENTE_RENTABLE" { "Yellow" }
                default              { "Green"  }
            }
            Write-Host ""
            Write-Host ("  --- {0}  [{1}] ---" -f $sym, $estadoActivo) -ForegroundColor $colSym
            Write-Host ("  {0,-35} {1,6} {2,8} {3,9} {4,7} {5,6}" -f `
                "Estrategia","Combos","MejorWR%","MejorPnL","MejorPF","OK") -ForegroundColor White
            Write-Host ("  " + "-" * 75) -ForegroundColor DarkGray
        }

        $pnlStr = if ($pnl -ge 0) { "+{0:N2}" -f $pnl } else { "{0:N2}" -f $pnl }
        $col = if ($wr -ge $BREAK_EVEN) { "Green" } else { "Red" }
        Write-Host ("  {0,-35} {1,6} {2,7}% {3,9} {4,7} {5,6}" -f `
            $est, $cmb, $wr, $pnlStr, $pf, ("{0}/{1}" -f $ok,$cmb)) -ForegroundColor $col
    }
}

# ============================================================
# RESUMEN FINAL
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  RESUMEN EJECUTIVO" -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

$totalActivos = $activos.Count
$rawCount = Invoke-SQL "SELECT COUNT(*) FROM public.backtest_sesiones WHERE total_operaciones >= $MinOps;"
$totalSesiones = ($rawCount | Where-Object { $_ -match '^\d+$' } | Select-Object -First 1)

Write-Host ("  Total activos evaluados : {0}" -f $totalActivos) -ForegroundColor White
Write-Host ("  Total sesiones validas  : {0}" -f $totalSesiones) -ForegroundColor White
Write-Host ("  Umbral de rentabilidad  : WR >= {0}%  (payout {1}%)" -f $BREAK_EVEN, $Payout) -ForegroundColor White
Write-Host ""

$colNunca = if ($nunca.Count -gt 0) { "Red" } else { "Green" }
$colRara  = if ($rara.Count -gt 0)  { "Yellow" } else { "Green" }
$listNunca = ($nunca | ForEach-Object { $_.Symbol }) -join ", "
$listRara  = ($rara  | Sort-Object MejorWR -Descending | ForEach-Object { $_.Symbol }) -join ", "
$listRent  = ($rent  | Sort-Object MejorWR -Descending | ForEach-Object { $_.Symbol }) -join ", "

Write-Host ("  Nunca rentables     : {0,2} activos  -> {1}" -f $nunca.Count, $listNunca) -ForegroundColor $colNunca
Write-Host ("  Raramente rentables : {0,2} activos  -> {1}" -f $rara.Count,  $listRara)  -ForegroundColor $colRara
Write-Host ("  Rentables           : {0,2} activos  -> {1}" -f $rent.Count,  $listRent)  -ForegroundColor Green

Write-Host ""
Write-Host "  RECOMENDACION:" -ForegroundColor Cyan

if ($nunca.Count -gt 0) {
    Write-Host ("    - Descartar: {0}" -f $listNunca) -ForegroundColor Red
    Write-Host "      Ninguna estrategia logra rentabilidad. Evitar operar en vivo." -ForegroundColor DarkRed
}
if ($rara.Count -gt 0) {
    Write-Host ("    - Monitorear: {0}" -f $listRara) -ForegroundColor Yellow
    Write-Host "      Solo funcionan con condiciones muy especificas. Monto minimo si se opera." -ForegroundColor DarkYellow
}
if ($rent.Count -gt 0) {
    Write-Host ("    - Operar: {0}" -f $listRent) -ForegroundColor Green
    Write-Host "      Multiples combinaciones rentables confirmadas. Priorizar en live test." -ForegroundColor DarkGreen
}

Write-Host ""
Write-Host ("  Tip: ejecuta con -Detalle para ver breakdown por estrategia") -ForegroundColor DarkGray
Write-Host ("  Tip: ajusta -MinOps N para cambiar el minimo de operaciones requeridas") -ForegroundColor DarkGray
Write-Host ""
