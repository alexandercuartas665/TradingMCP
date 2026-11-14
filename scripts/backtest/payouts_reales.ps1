# Mide el payout real que ofrece Deriv (cotizaciones CALL/PUT, NO compra nada).
# Requiere la app en modo agente (API 8766) con trading conectado en demo:
#   POST /api/heavy/trading/conectar {"demo":true}
# Los mercados de divisas y metales cierran el fin de semana: medirlos de lunes a viernes.
# Resultados en docs/validacion/payouts_reales.json (se acumulan las mediciones).
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')
$simbolos = @('OTC_NDX','OTC_DJI','OTC_SPC','frxEURUSD','frxGBPJPY','frxGBPUSD','frxUSDCHF','frxUSDJPY','frxUSOIL','frxXAGUSD','frxXAUUSD','R_100')
# Duraciones: 1, 2, 3 y 4 velas de 1h y 2 velas de 4h (las estrategias validadas usan 2 velas)
$mercados = @()
foreach ($s in $simbolos) {
  foreach ($v in 1..4) { $mercados += ,@($s, '1h', $v) }
  $mercados += ,@($s, '4h', 2)
}
$body = @{ mercados = $mercados; monto = 10 } | ConvertTo-Json -Depth 4 -Compress
$r = Invoke-RestMethod -Uri 'http://localhost:8766/api/heavy/payouts' -Method POST -Body $body -ContentType 'application/json' -TimeoutSec 900
if (-not $r.ok) { Write-Output ("ERROR: {0}" -f $r.error); exit 1 }
Write-Output ("Medido {0} (cuenta {1}, monto {2})" -f $r.medido_utc, $r.cuenta, $r.monto)
foreach ($m in $r.mercados) {
  if ($null -ne $m.payout_pct) {
    Write-Output ("{0,-10} {1,-3} x{2} ({3,5}) | CALL {4,6} PUT {5,6} | medio {6,6}% -> minimo para no perder {7:P1}" -f `
      $m.symbol, $m.timeframe, $m.velas, $m.duracion, $m.call_payout_pct, $m.put_payout_pct, $m.payout_pct, $m.punto_equilibrio)
  } else {
    $err = @($m.error, $m.call_error, $m.put_error) | Where-Object { $_ } | Select-Object -First 1
    Write-Output ("{0,-10} {1,-3} x{2} ({3,5}) | sin cotizacion: {4}" -f $m.symbol, $m.timeframe, $m.velas, $m.duracion, $err)
  }
}
$ruta = Join-Path $PSScriptRoot '..\..\docs\validacion\payouts_reales.json'
$historial = @()
if (Test-Path $ruta) { $historial = @(Get-Content $ruta -Raw | ConvertFrom-Json) }
$historial += $r
ConvertTo-Json -InputObject $historial -Depth 6 | Set-Content -Encoding utf8 $ruta
