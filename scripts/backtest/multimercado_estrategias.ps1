# La misma regla en todos los mercados/temporalidades con historial.
# Compara el periodo de diseno (hasta el corte) con el periodo ciego (despues).
# Requiere la app en modo agente (API 8766). Resultados en docs/validacion/multimercado_resultados.json
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')
$simbolos = @('OTC_NDX','OTC_DJI','OTC_SPC','frxEURUSD','frxGBPJPY','frxGBPUSD','frxUSDCHF','frxUSDJPY','frxUSOIL','frxXAGUSD','frxXAUUSD','R_100')
$mercados = @()
foreach ($s in $simbolos) { foreach ($t in @('1h','4h')) { $mercados += ,@($s, $t) } }
$estrategias = @(
  @('BollingerMomentum_v2',2,50,85),
  @('IndicePullbackLong_v1',2,50,85),
  @('CCIReversal_v2',2,55,85),
  @('EmaRsiCruce_v4',2,55,85)
)
$todo = @()
foreach ($e in $estrategias) {
  $body = @{ estrategia=$e[0]; duracion=$e[1]; confianza=$e[2]; payout=$e[3]; corte='2026-04-29'; mercados=$mercados } | ConvertTo-Json -Depth 4 -Compress
  $r = Invoke-RestMethod -Uri 'http://localhost:8766/api/heavy/multimercado' -Method POST -Body $body -ContentType 'application/json' -TimeoutSec 3600
  $todo += $r
  if (-not $r.ok) { Write-Output ("{0}: ERROR {1}" -f $e[0], $r.error); continue }
  Write-Output ("{0,-23} diseno: {1,4} ops {2,6:P1} | ciego: {3,4} ops {4,6:P1} IC {5:P1}-{6:P1} {7} | mercados sobre equilibrio en ciego: {8}/{9}" -f `
    $e[0], $r.total_diseno.operaciones, $r.total_diseno.win_rate, $r.total_ciego.operaciones, $r.total_ciego.win_rate,
    $r.total_ciego.ic95_inf, $r.total_ciego.ic95_sup, $r.total_ciego.veredicto,
    $r.mercados_sobre_equilibrio_en_ciego, $r.mercados_con_datos_ciegos)
}
$todo | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $PSScriptRoot '..\..\docs\validacion\multimercado_resultados.json')
