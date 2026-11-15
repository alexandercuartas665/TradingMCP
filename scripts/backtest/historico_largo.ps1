# Prueba pre-registrada con historico largo (docs/validacion/historico_largo_plan.md).
# Mide cada configuracion en Dukascopy 2003-05-01 a 2024-03-31 (nunca usado para disenar)
# y compara Deriv vs Dukascopy desde 2024-04-01 como control de la fuente.
# Requiere la app en modo agente (API 8766) y el historico externo descargado.
#
#   pwsh -File scripts\backtest\historico_largo.ps1
param(
  [string]$Salida = (Join-Path $PSScriptRoot '..\..\docs\validacion\historico_largo_2026-11-15'),
  [string]$Corte = '2024-04-01'
)
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')
$api = 'http://localhost:8766/api/heavy/validar'

$configs = @(
  @('BollingerMomentum_v2', 'OTC_NDX', '1h', 50), @('BollingerMomentum_v2', 'OTC_NDX', '1h', 60),
  @('IndicePullbackLong_v1', 'OTC_NDX', '4h', 50), @('IndicePullbackLong_v1', 'OTC_DJI', '4h', 50),
  @('IndicePullbackLong_v1', 'OTC_SPC', '4h', 50), @('CCIReversal_v2', 'OTC_DJI', '4h', 55),
  @('EmaRsiCruce_v4', 'OTC_NDX', '4h', 55), @('BollingerRsiReversal_d30', 'frxXAUUSD', '1h', 50),
  @('BollingerRsiReversal', 'frxEURUSD', '4h', 50),
  @('IndicePullbackLong_v1', 'frxUSDJPY', '1h', 50), @('IndicePullbackLong_v1', 'frxGBPUSD', '1h', 50),
  @('EmaRsiCruce_v4', 'frxUSDJPY', '1h', 55), @('EmaRsiCruce_v4', 'frxGBPUSD', '1h', 55),
  @('LondresMomentum_v1', 'frxEURUSD', '1h', 50), @('LondresMomentum_v1', 'frxGBPUSD', '1h', 50),
  @('AsiaReversion_v1', 'frxEURUSD', '1h', 50), @('AsiaReversion_v1', 'frxGBPUSD', '1h', 50),
  @('SobrerreaccionVela_v1', 'frxEURUSD', '1h', 50), @('SobrerreaccionVela_v1', 'frxGBPUSD', '1h', 50),
  @('SobrerreaccionVela_v1', 'frxUSDJPY', '1h', 50), @('SobrerreaccionVela_v1', 'frxXAUUSD', '1h', 50)
)
$zCritico = 2.82   # Bonferroni 0.05/21 unilateral
$eq85 = 100.0 / 185.0
$eq78 = 100.0 / 178.0

function Validar($est, $sym, $tf, $conf, $fuente, $desde, $corte) {
  $b = @{ estrategia = $est; symbol = $sym; timeframe = $tf; duracion = 2; confianza = $conf; payout = 85; corte = $corte; desde = $desde; fuente = $fuente }
  try { return Invoke-RestMethod $api -Method POST -Body ($b | ConvertTo-Json -Compress) -ContentType 'application/json' -TimeoutSec 1800 }
  catch { return @{ ok = $false; error = $_.Exception.Message } }
}

function Z($g, $n, $eq) { if ($n -le 0) { return 0.0 }; return (($g / $n) - $eq) / [math]::Sqrt($eq * (1 - $eq) / $n) }

$filas = @()
$lineas = @('PRUEBA CON HISTORICO LARGO - plan pre-registrado en docs/validacion/historico_largo_plan.md',
  ("Periodo limpio: Dukascopy 2003-05-01 a {0} (nunca usado para disenar). Payout supuesto 85% (equilibrio 54,05%); con 78% el equilibrio es 56,18%." -f ([datetime]$Corte).AddDays(-1).ToString('yyyy-MM-dd')),
  "Ventaja = z >= $zCritico sobre el equilibrio (Bonferroni, 21 pruebas).", '')
$i = 0
foreach ($c in $configs) {
  $i++
  $est, $sym, $tf, $conf = $c
  $limpio = Validar $est $sym $tf $conf 'Dukascopy' '2003-05-01' $Corte
  $ctlDuka = Validar $est $sym $tf $conf 'Dukascopy' $Corte '2026-04-29'
  $ctlDeriv = Validar $est $sym $tf $conf 'Deriv' $Corte '2026-04-29'
  if (-not $limpio.ok) {
    $lineas += ("{0,2}. {1} {2} {3} conf {4}: ERROR {5}" -f $i, $est, $sym, $tf, $conf, $limpio.error)
    $filas += [ordered]@{ n = $i; estrategia = $est; symbol = $sym; timeframe = $tf; confianza = $conf; error = $limpio.error }
    continue
  }
  $d = $limpio.periodo_diseno
  $n = [int]$d.operaciones; $g = [int]$d.ganadas
  $wr = if ($n -gt 0) { $g / $n } else { 0 }
  $z85 = Z $g $n $eq85
  $z78 = Z $g $n $eq78
  $veredicto = if ($n -lt 30) { 'MUESTRA INSUFICIENTE' } elseif ($z85 -ge $zCritico -and $wr -gt $eq78) { 'VENTAJA (corregida)' } elseif ($wr -gt $eq85) { 'POSITIVO, NO SIGNIFICATIVO' } else { 'SIN VENTAJA' }
  $cd = $ctlDuka.periodo_diseno; $cv = $ctlDeriv.periodo_diseno
  $fmt = { param($r) if ($r -and [int]$r.operaciones -gt 0) { "{0} ops {1:N1}%" -f $r.operaciones, ([double]$r.win_rate * 100) } else { 'sin ops' } }
  $lineas += ("{0,2}. {1,-24} {2,-10} {3} conf {4}: {5,5} ops, acierto {6:N1}% (IC95 {7:N1}-{8:N1}), z85 {9:N2}, P&L a 1 USD {10} -> {11}" -f `
      $i, $est, $sym, $tf, $conf, $n, ($wr * 100), ([double]$d.ic95_inf * 100), ([double]$d.ic95_sup * 100), $z85, $d.pnl_por_dolar, $veredicto)
  $lineas += ("     desde {0} | control 2024-04 a 2026-04: Deriv {1} / Dukascopy {2}" -f $d.desde, (& $fmt $cv), (& $fmt $cd))
  $filas += [ordered]@{ n = $i; estrategia = $est; symbol = $sym; timeframe = $tf; confianza = $conf
    limpio = $d; z85 = [math]::Round($z85, 3); z78 = [math]::Round($z78, 3); veredicto = $veredicto
    control_deriv = $cv; control_dukascopy = $cd }
  Write-Output ("{0,2}/21 {1} {2} {3}: {4} ops {5:N1}% -> {6}" -f $i, $est, $sym, $tf, $n, ($wr * 100), $veredicto)
}
$tot = $filas | Where-Object { $_.limpio }
$N = ($tot | ForEach-Object { [int]$_.limpio.operaciones } | Measure-Object -Sum).Sum
$G = ($tot | ForEach-Object { [int]$_.limpio.ganadas } | Measure-Object -Sum).Sum
$lineas += ''
$lineas += ("TOTAL (todas juntas): {0} operaciones, acierto {1:N2}%  (equilibrio 54,05% con payout 85%, 56,18% con 78%)" -f $N, $(if ($N) { $G / $N * 100 } else { 0 }))
$lineas += ("Con ventaja corregida: {0} de {1}" -f @($tot | Where-Object { $_.veredicto -like 'VENTAJA*' }).Count, $tot.Count)
$lineas | Set-Content -Encoding utf8 ($Salida + '.txt')
$filas | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 ($Salida + '.json')
Write-Output ''
$lineas | Select-Object -Last 2
