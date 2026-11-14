# Las estrategias principales operadas con MULTIPLICADORES en vez de Rise/Fall.
# En cada mercado se elige la mejor combinacion (multiplicador, take profit, stop loss,
# velas maximas) SOLO con el periodo de diseno y se mide esa misma en el periodo ciego.
# Requiere la app en modo agente (API 8766). Resultados en docs/validacion/multiplicadores_resultados.json
#
# Comision (fraccion del nocional = stake x multiplicador):
#   R_100: 0.00055 medido con cotizaciones reales (POST /api/heavy/multiplicadores/cotizar).
#   Divisas y metales: se miden con el mercado abierto. Por defecto 0 = resultado BRUTO
#   (si no hay ventaja ni sin comision, tampoco la habra con ella).
param([double]$ComisionForex = 0.0)

. (Join-Path $PSScriptRoot '..\agent_auth.ps1')
$forex = @('frxEURUSD','frxGBPJPY','frxGBPUSD','frxUSDCHF','frxUSDJPY','frxXAUUSD','frxXAGUSD')
$mercados = @()
foreach ($t in @('1h','4h')) {
  foreach ($s in $forex) { $mercados += ,@($s, $t, $ComisionForex, @(100,200,500)) }
  $mercados += ,@('R_100', $t, 0.00055, @(40,100,200))
}
# TP y SL en multiplos del stake ([0,0] = sin TP, solo stop out y cierre por tiempo)
$tpSl = @(@(0,0), @(0.5,0.5), @(1,0.5), @(2,1), @(1,1), @(0.5,1), @(3,1))
$estrategias = @(
  @('BollingerMomentum_v2',50),
  @('IndicePullbackLong_v1',50),
  @('CCIReversal_v2',55),
  @('EmaRsiCruce_v4',55)
)
$todo = @()
foreach ($e in $estrategias) {
  $body = @{ estrategia=$e[0]; confianza=$e[1]; corte='2026-04-29'; min_ops=30; mercados=$mercados;
             multiplicadores=@(100); tp_sl=$tpSl; max_velas=@(2,6,24) } | ConvertTo-Json -Depth 5 -Compress
  $r = Invoke-RestMethod -Uri 'http://localhost:8766/api/heavy/multiplicadores/validar' -Method POST -Body $body -ContentType 'application/json' -TimeoutSec 7200
  $todo += $r
  if (-not $r.ok) { Write-Output ("{0}: ERROR {1}" -f $e[0], $r.error); continue }
  $d = $r.total_diseno; $c = $r.total_ciego; $o = $r.total_ciego_solo_rentables_en_diseno
  Write-Output ("{0,-23} diseno: {1,5} ops medio {2,7:N3} | ciego: {3,4} ops acierto {4,6:P1} medio {5,7:N3} IC {6:N3}/{7:N3} {8} | solo rentables en diseno ({9}): {10} ops medio {11:N3} {12}" -f `
    $e[0], $d.operaciones, $d.pnl_medio, $c.operaciones, $c.win_rate, $c.pnl_medio, $c.ic95_inf, $c.ic95_sup, $c.veredicto,
    $r.mercados_rentables_en_diseno, $o.operaciones, $o.pnl_medio, $o.veredicto)
  foreach ($m in $r.mercados) {
    if ($m.error) { continue }
    $k = $m.config_elegida
    Write-Output ("    {0,-10} {1,-3} x{2,-3} TP {3,-3} SL {4,-3} {5,2}v | diseno {6,4} ops {7,7:N3} | ciego {8,3} ops {9,6:P1} medio {10,7:N3} pf {11,5} {12}" -f `
      $m.symbol, $m.timeframe, $k.multiplicador, $k.take_profit, $k.stop_loss, $k.max_velas,
      $m.diseno.operaciones, $m.diseno.pnl_medio, $m.ciego.operaciones, $m.ciego.win_rate, $m.ciego.pnl_medio, $m.ciego.profit_factor, $m.ciego.veredicto)
  }
}
$todo | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $PSScriptRoot '..\..\docs\validacion\multiplicadores_resultados.json')
