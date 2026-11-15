# Corre un lote pre-registrado de hipotesis (JSON en docs/validacion/) sobre el historico
# largo de Dukascopy y juzga cada idea sumando sus mercados, con correccion por pruebas
# multiples. Requiere la app en modo agente (API 8766).
#
#   pwsh -File scripts\backtest\lote_hipotesis.ps1 -Lote docs\validacion\lote2_hipotesis.json
param(
  [Parameter(Mandatory = $true)][string]$Lote,
  [double]$Payout = 85
)
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')
$api = 'http://localhost:8766/api/heavy/validar'
$repo = Join-Path $PSScriptRoot '..\..'
$def = Get-Content -Raw -Encoding utf8 (Resolve-Path $Lote) | ConvertFrom-Json
$salida = Join-Path $repo ("docs\validacion\" + $def.nombre)
$eq = 100.0 / (100.0 + $Payout)
$eq78 = 100.0 / 178.0

# Normal estandar: CDF (Abramowitz-Stegun 7.1.26) e inversa por biseccion
function Phi([double]$x) {
  $t = 1.0 / (1.0 + 0.3275911 * [math]::Abs($x) / [math]::Sqrt(2))
  $y = 1.0 - (((((1.061405429 * $t - 1.453152027) * $t) + 1.421413741) * $t - 0.284496736) * $t + 0.254829592) * $t * [math]::Exp(-$x * $x / 2)
  if ($x -ge 0) { return 0.5 * (1 + $y) } else { return 0.5 * (1 - $y) }
}
function ZCritico([int]$pruebas) {
  $obj = 1.0 - 0.05 / $pruebas; $lo = 0.0; $hi = 8.0
  for ($k = 0; $k -lt 60; $k++) { $m = ($lo + $hi) / 2; if ((Phi $m) -lt $obj) { $lo = $m } else { $hi = $m } }
  return [math]::Round($hi, 2)
}
function Z($g, $n, $e) { if ($n -le 0) { return 0.0 }; return (($g / $n) - $e) / [math]::Sqrt($e * (1 - $e) / $n) }
function Validar($est, $sym, $tf, $dur, $fuente, $desde, $corte) {
  $b = @{ estrategia = $est; symbol = $sym; timeframe = $tf; duracion = $dur; confianza = 50; payout = $Payout; corte = $corte; desde = $desde; fuente = $fuente }
  try { return Invoke-RestMethod $api -Method POST -Body ($b | ConvertTo-Json -Compress) -ContentType 'application/json' -TimeoutSec 1800 }
  catch { return [pscustomobject]@{ ok = $false; error = $_.Exception.Message } }
}
function Txt($r) { if ($r -and $r.ok -and [int]$r.periodo_diseno.operaciones -gt 0) { "{0} ops {1:N1}%" -f $r.periodo_diseno.operaciones, ([double]$r.periodo_diseno.win_rate * 100) } else { 'sin ops' } }

$nPruebas = ($def.pruebas | ForEach-Object { $_.mercados.Count } | Measure-Object -Sum).Sum
$nIdeas = $def.pruebas.Count
$zIdea = ZCritico $nIdeas
$zMercado = ZCritico $nPruebas
$finLimpio = ([datetime]$def.corte).AddDays(-1).ToString('yyyy-MM-dd')

$lineas = @("LOTE $($def.nombre) - plan pre-registrado en docs/validacion/",
  "Periodo limpio: Dukascopy $($def.desde) a $finLimpio. Payout supuesto $Payout% (equilibrio $([math]::Round($eq*100,2))%); con 78% el equilibrio es 56,18%.",
  "Ventaja por idea: z >= $zIdea ($nIdeas ideas) y acierto > 56,18%. Por mercado: z >= $zMercado ($nPruebas pruebas).", '')
$resultado = @()
foreach ($p in $def.pruebas) {
  $sumG = 0; $sumN = 0; $filas = @()
  foreach ($sym in $p.mercados) {
    $r = Validar $p.estrategia $sym $p.tf $p.dur 'Dukascopy' $def.desde $def.corte
    $cv = Validar $p.estrategia $sym $p.tf $p.dur 'Deriv' $def.corte '2026-04-29'
    $cd = Validar $p.estrategia $sym $p.tf $p.dur 'Dukascopy' $def.corte '2026-04-29'
    if (-not $r.ok) { $filas += ("     {0,-10} ERROR {1}" -f $sym, $r.error); continue }
    $d = $r.periodo_diseno; $n = [int]$d.operaciones; $g = [int]$d.ganadas
    $sumG += $g; $sumN += $n
    $z = Z $g $n $eq
    $marca = if ($n -ge 30 -and $z -ge $zMercado -and ($g / [math]::Max(1, $n)) -gt $eq78) { ' <- ventaja en este mercado' } else { '' }
    $filas += ("     {0,-10} {1,5} ops {2,5:N1}% (IC95 {3:N1}-{4:N1}) z {5,5:N2} desde {6} | control 2024-26 Deriv {7} / Dukascopy {8}{9}" -f `
        $sym, $n, ($(if ($n) { $g / $n * 100 } else { 0 })), ([double]$d.ic95_inf * 100), ([double]$d.ic95_sup * 100), $z, $d.desde, (Txt $cv), (Txt $cd), $marca)
    $resultado += [pscustomobject]@{ idea = $p.idea; estrategia = $p.estrategia; symbol = $sym; tf = $p.tf; dur = $p.dur; limpio = $d; z = [math]::Round($z, 3)
      control_deriv = $(if ($cv.ok) { $cv.periodo_diseno }); control_dukascopy = $(if ($cd.ok) { $cd.periodo_diseno }) }
  }
  $wr = if ($sumN) { $sumG / $sumN } else { 0 }
  $zi = Z $sumG $sumN $eq
  $ver = if ($sumN -lt 30) { 'MUESTRA INSUFICIENTE' } elseif ($zi -ge $zIdea -and $wr -gt $eq78) { 'VENTAJA (corregida)' } elseif ($wr -gt $eq) { 'POSITIVO, NO SIGNIFICATIVO' } else { 'SIN VENTAJA' }
  $lineas += ("{0,2}. {1,-26} {2} dur {3}: {4,6} ops, acierto {5:N2}%, z {6:N2} -> {7}" -f $p.idea, $p.estrategia, $p.tf, $p.dur, $sumN, ($wr * 100), $zi, $ver)
  $lineas += $filas
  Write-Output ("{0,2}/{1} {2}: {3} ops {4:N1}% -> {5}" -f $p.idea, $nIdeas, $p.estrategia, $sumN, ($wr * 100), $ver)
}
if ($def.controles) {
  $lineas += ''; $lineas += 'CONTROLES (no cuentan como pruebas):'
  foreach ($c in $def.controles) {
    foreach ($sym in $c.mercados) {
      $r = Validar $c.estrategia $sym $c.tf $c.dur 'Dukascopy' $def.desde $def.corte
      $lineas += ("     {0} {1} {2} dur {3}: {4} ({5})" -f $c.estrategia, $sym, $c.tf, $c.dur, (Txt $r), $c.nota)
    }
  }
}
$tot = ($resultado | ForEach-Object { [int]$_.limpio.operaciones } | Measure-Object -Sum).Sum
$gan = ($resultado | ForEach-Object { [int]$_.limpio.ganadas } | Measure-Object -Sum).Sum
$lineas += ''
$lineas += ("TOTAL: {0} operaciones, acierto {1:N2}%. Ideas con ventaja corregida: {2} de {3}." -f $tot, ($(if ($tot) { $gan / $tot * 100 } else { 0 })),
  @($lineas | Where-Object { $_ -match '^\s*\d+\..*VENTAJA \(corregida\)' }).Count, $nIdeas)
$lineas | Set-Content -Encoding utf8 ($salida + '.txt')
$resultado | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 ($salida + '.json')
Write-Output ''
Write-Output $lineas[-1]
