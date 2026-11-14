# Walk-forward + Monte Carlo + ruido en precios para las configuraciones principales.
# Requiere la app en modo agente (API 8766). Resultados en docs/validacion/robustez_resultados.json
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')
$casos = @(
  @('BollingerMomentum_v2','OTC_NDX','1h',2,50,85),
  @('IndicePullbackLong_v1','OTC_NDX','4h',2,50,85),
  @('CCIReversal_v2','OTC_DJI','4h',2,55,90),
  @('EmaRsiCruce_v4','OTC_NDX','4h',2,55,85),
  @('BollingerRsiReversal_d30','frxXAUUSD','1h',2,50,85),
  @('EmaRsiCruce_v4','R_100','4h',2,55,85)
)
$todo = @()
foreach ($c in $casos) {
  $body = @{ estrategia=$c[0]; symbol=$c[1]; timeframe=$c[2]; duracion=$c[3]; confianza=$c[4]; payout=$c[5];
             dias_entrenamiento=365; dias_prueba=90; grilla=@(50,55,60,65,70); iteraciones=5000; corridas_ruido=30; nivel_ruido=0.1; corte='2026-04-29' } | ConvertTo-Json -Compress
  $r = Invoke-RestMethod -Uri 'http://localhost:8766/api/heavy/robustez' -Method POST -Body $body -ContentType 'application/json' -TimeoutSec 7200
  $todo += $r
  if (-not $r.ok) { Write-Output ("{0} {1} {2}: ERROR {3}" -f $c[0], $c[1], $c[2], $r.error); continue }
  $wf = $r.walk_forward.total_prueba; $mc = $r.monte_carlo; $ru = $r.ruido_precios
  Write-Output ("{0,-25} {1,-13} | WF prueba: {2,3} ops {3,5:P1} IC {4:P0}-{5:P0} pnl {6,6} {7} | MC: P(ganar) {8:P0}, caida p95 {9}, racha p95 {10} | Ruido: base {11:P1} -> {12:P1} +/- {13:P1}, {14}/30 sobre equilibrio" -f `
    $c[0], "$($c[1]) $($c[2])", $wf.operaciones, $wf.win_rate, $wf.ic95_inf, $wf.ic95_sup, $wf.pnl_por_dolar, $wf.veredicto,
    $mc.bootstrap.prob_terminar_ganando, $mc.orden.caida_maxima_p95, $mc.orden.peor_racha_p95,
    $ru.base_win_rate, $ru.ruido_win_rate_media, $ru.ruido_win_rate_desv, $ru.corridas_sobre_equilibrio)
  $d = $r.walk_forward.total_prueba_despues_del_corte; $a = $r.walk_forward.total_prueba_antes_del_corte
  Write-Output ("    WF antes del corte: {0} ops {1:P1}  |  WF despues del corte (limpio): {2} ops {3:P1} IC {4:P0}-{5:P0} {6}" -f $a.operaciones, $a.win_rate, $d.operaciones, $d.win_rate, $d.ic95_inf, $d.ic95_sup, $d.veredicto)
}
$todo | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $PSScriptRoot '..\..\docs\validacion\robustez_resultados.json')
