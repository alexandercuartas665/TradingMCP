# Prueba pre-registrada: multiplicadores con historico largo
# (docs/validacion/multiplicadores_historico_largo_plan.md).
#  A) configuracion fija elegida con Deriv el 2026-11-14 -> Dukascopy 2003-05-01 a 2024-04-01,
#     con comision 0 / 0.01% / 0.02% / 0.04% del nocional. Control: 2024-04-01 a 2026-04-29.
#  B) walk-forward dentro de Dukascopy: elegir en 2003-2013, medir en 2014-2024 (bruto).
# Las 4 estrategias corren en paralelo (una llamada a la vez por estrategia).
# Requiere la app en modo agente (API 8766) y el historico Dukascopy descargado.
#
#   pwsh -File scripts\backtest\multiplicadores_historico_largo.ps1
param(
  [string]$Salida = (Join-Path $PSScriptRoot '..\..\docs\validacion\multiplicadores_historico_largo_2026-11-16')
)
$auth = (Resolve-Path (Join-Path $PSScriptRoot '..\agent_auth.ps1')).Path
$previo = Get-Content (Join-Path $PSScriptRoot '..\..\docs\validacion\multiplicadores_resultados.json') -Raw | ConvertFrom-Json
$orden = @('BollingerMomentum_v2', 'IndicePullbackLong_v1', 'CCIReversal_v2', 'EmaRsiCruce_v4')
$confs = @{ 'BollingerMomentum_v2' = 50; 'IndicePullbackLong_v1' = 50; 'CCIReversal_v2' = 55; 'EmaRsiCruce_v4' = 55 }

$trabajos = foreach ($nombre in $orden) {
  [pscustomobject]@{ nombre = $nombre; conf = $confs[$nombre]; previo = ($previo | Where-Object { $_.estrategia -eq $nombre }) }
}

$res = $trabajos | ForEach-Object -ThrottleLimit 4 -Parallel {
  . $using:auth
  $api = 'http://localhost:8766/api/heavy/multiplicadores/validar'
  $zCritico = 2.24   # Bonferroni 0.05/4 unilateral
  $comisiones = @(0.0, 0.0001, 0.0002, 0.0004)
  $nombre = $_.nombre; $conf = $_.conf; $p = $_.previo
  function Llamar($body) {
    try { return Invoke-RestMethod $api -Method POST -Body ($body | ConvertTo-Json -Depth 6 -Compress) -ContentType 'application/json' -TimeoutSec 21600 }
    catch { return [pscustomobject]@{ ok = $false; error = $_.Exception.Message } }
  }
  function ZDe($resumen) {
    $se = ([double]$resumen.ic95_sup - [double]$resumen.ic95_inf) / 3.92
    if ($se -le 0) { return 0.0 }
    return [double]$resumen.pnl_medio / $se
  }

  # ---------- Prueba A ----------
  $fijos = @($p.mercados | Where-Object { $_.config_elegida -and $_.symbol -like 'frx*' })
  $lineasA = @('', ("{0} ({1} mercados con configuracion fija)" -f $nombre, $fijos.Count))
  $filaA = [ordered]@{ estrategia = $nombre; mercados = $fijos.Count; niveles = @() }
  foreach ($com in $comisiones) {
    $mercados = @()
    foreach ($m in $fijos) {
      $k = $m.config_elegida
      $mercados += , @($m.symbol, $m.timeframe, $com, @([int]$k.multiplicador), @(, @([double]$k.take_profit, [double]$k.stop_loss)), @([int]$k.max_velas))
    }
    $r = Llamar @{ estrategia = $nombre; confianza = $conf; fuente = 'Dukascopy'; desde = '2003-05-01'; hasta = '2026-04-29';
                   corte = '2024-04-01'; min_ops = 1; mercados = $mercados }
    if (-not $r.ok) { $lineasA += ("  comision {0:P2}: ERROR {1}" -f $com, $r.error); continue }
    $t = $r.total_diseno
    $z = ZDe $t
    $positivos = @($r.mercados | Where-Object { -not $_.error -and [double]$_.diseno.pnl_medio -gt 0 }).Count
    $validos = @($r.mercados | Where-Object { -not $_.error }).Count
    $lineasA += ("  comision {0,6:P2}: {1,6} ops  acierto {2,6:P1}  medio {3,7:N4}  IC {4,7:N4} / {5,7:N4}  z {6,5:N2}  pf {7,5}  mercados positivos {8}/{9} | control 2024-04 a 2026-04: {10} ops medio {11,7:N4}" -f `
      $com, $t.operaciones, $t.win_rate, $t.pnl_medio, $t.ic95_inf, $t.ic95_sup, $z, $t.profit_factor, $positivos, $validos,
      $r.total_ciego.operaciones, $r.total_ciego.pnl_medio)
    if ($com -eq 0.0) {
      foreach ($m in $r.mercados) {
        if ($m.error) { $lineasA += ("      {0,-10} {1,-3} ERROR {2}" -f $m.symbol, $m.timeframe, $m.error); continue }
        $k = $m.config_elegida
        $antes = $fijos | Where-Object { $_.symbol -eq $m.symbol -and $_.timeframe -eq $m.timeframe }
        $lineasA += ("      {0,-10} {1,-3} x{2,-3} TP {3,-3} SL {4,-3} {5,2}v | 2003-2024: {6,5} ops medio {7,7:N4} IC {8,7:N4}/{9,7:N4} | control Duka {10,4} ops {11,7:N4} vs Deriv diseno {12,4} ops {13,7:N4}" -f `
          $m.symbol, $m.timeframe, $k.multiplicador, $k.take_profit, $k.stop_loss, $k.max_velas,
          $m.diseno.operaciones, $m.diseno.pnl_medio, $m.diseno.ic95_inf, $m.diseno.ic95_sup,
          $m.ciego.operaciones, $m.ciego.pnl_medio, $antes.diseno.operaciones, $antes.diseno.pnl_medio)
      }
      $filaA.z_bruto = [math]::Round($z, 3)
      $filaA.positivos = $positivos
      $filaA.validos = $validos
      $filaA.control_deriv_diseno = $p.total_diseno
    }
    $filaA.niveles += [ordered]@{ comision = $com; z = [math]::Round($z, 3); positivos = $positivos; resultado = $r }
  }
  $ventaja = ($null -ne $filaA.z_bruto) -and ($filaA.z_bruto -ge $zCritico) -and ($filaA.positivos -gt ($filaA.validos / 2))
  $aguanta = @($filaA.niveles | Where-Object { [double]$_.resultado.total_diseno.pnl_medio -gt 0 } | ForEach-Object { $_.comision })
  $maxCom = if ($aguanta.Count) { ($aguanta | Measure-Object -Maximum).Maximum } else { $null }
  $filaA.veredicto = if ($ventaja) { 'VENTAJA' } else { 'SIN VENTAJA DEMOSTRADA' }
  $filaA.comision_max_con_media_positiva = $maxCom
  $lineasA += ("  => {0}{1}" -f $filaA.veredicto, $(if ($null -ne $maxCom) { (" (media > 0 hasta comision {0:P2})" -f $maxCom) } else { ' (media <= 0 incluso en bruto)' }))

  # ---------- Prueba B ----------
  $forex = @('frxEURUSD', 'frxGBPJPY', 'frxGBPUSD', 'frxUSDCHF', 'frxUSDJPY', 'frxXAUUSD', 'frxXAGUSD')
  $mercadosB = @()
  foreach ($tf in @('1h', '4h')) { foreach ($s in $forex) { $mercadosB += , @($s, $tf, 0.0, @(100, 200, 500)) } }
  $tpSl = @(@(0, 0), @(0.5, 0.5), @(1, 0.5), @(2, 1), @(1, 1), @(0.5, 1), @(3, 1))
  $r = Llamar @{ estrategia = $nombre; confianza = $conf; fuente = 'Dukascopy'; desde = '2003-05-01'; hasta = '2024-04-01';
                 corte = '2014-01-01'; min_ops = 30; mercados = $mercadosB; multiplicadores = @(100); tp_sl = $tpSl; max_velas = @(2, 6, 24) }
  $lineasB = @()
  $filaB = $null
  if (-not $r.ok) { $lineasB += ("{0}: ERROR {1}" -f $nombre, $r.error) }
  else {
    $c = $r.total_ciego
    $z = ZDe $c
    $positivos = @($r.mercados | Where-Object { -not $_.error -and [double]$_.ciego.pnl_medio -gt 0 }).Count
    $validos = @($r.mercados | Where-Object { -not $_.error }).Count
    $ventajaB = ($z -ge $zCritico) -and ($positivos -gt $validos / 2)
    $verB = if ($ventajaB) { 'VENTAJA' } else { 'SIN VENTAJA DEMOSTRADA' }
    $lineasB += ("{0,-23} eleccion 2003-2013: {1,6} ops medio {2,7:N4} | medicion 2014-2024: {3,6} ops acierto {4,6:P1} medio {5,7:N4} IC {6,7:N4}/{7,7:N4} z {8,5:N2} pf {9,5} positivos {10}/{11} => {12}" -f `
      $nombre, $r.total_diseno.operaciones, $r.total_diseno.pnl_medio, $c.operaciones, $c.win_rate, $c.pnl_medio, $c.ic95_inf, $c.ic95_sup, $z,
      $c.profit_factor, $positivos, $validos, $verB)
    foreach ($m in $r.mercados) {
      if ($m.error) { continue }
      $k = $m.config_elegida
      $lineasB += ("      {0,-10} {1,-3} x{2,-3} TP {3,-3} SL {4,-3} {5,2}v | eleccion {6,5} ops {7,7:N4} | medicion {8,5} ops medio {9,7:N4}" -f `
        $m.symbol, $m.timeframe, $k.multiplicador, $k.take_profit, $k.stop_loss, $k.max_velas,
        $m.diseno.operaciones, $m.diseno.pnl_medio, $m.ciego.operaciones, $m.ciego.pnl_medio)
    }
    $filaB = [ordered]@{ estrategia = $nombre; z = [math]::Round($z, 3); positivos = $positivos; validos = $validos; veredicto = $verB; resultado = $r }
  }
  [pscustomobject]@{ nombre = $nombre; lineasA = $lineasA; filaA = $filaA; lineasB = $lineasB; filaB = $filaB }
}

$porNombre = @{}
foreach ($x in $res) { $porNombre[$x.nombre] = $x }
$lineas = @('MULTIPLICADORES CON HISTORICO LARGO - plan pre-registrado en docs/validacion/multiplicadores_historico_largo_plan.md',
  'Resultados por cada 1 USD de stake. Ventaja = z >= 2,24 en bruto (Bonferroni, 4 estrategias) y media > 0 en mas de la mitad de los mercados.',
  '', 'PRUEBA A: configuracion fija (elegida con Deriv), Dukascopy 2003-05-01 a 2024-04-01')
foreach ($n in $orden) { $lineas += $porNombre[$n].lineasA }
$lineas += ''
$lineas += 'PRUEBA B: walk-forward dentro de Dukascopy (mejor de 63 combinaciones en 2003-05-01 a 2014-01-01, medida en 2014-01-01 a 2024-04-01, bruto)'
foreach ($n in $orden) { $lineas += $porNombre[$n].lineasB }
$json = [ordered]@{ prueba_a = @($orden | ForEach-Object { $porNombre[$_].filaA }); prueba_b = @($orden | ForEach-Object { $porNombre[$_].filaB }) }
$lineas | Set-Content -Encoding utf8 ($Salida + '.txt')
$json | ConvertTo-Json -Depth 14 | Set-Content -Encoding utf8 ($Salida + '.json')
$lineas
