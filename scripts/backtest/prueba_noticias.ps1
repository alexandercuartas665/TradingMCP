# Prueba pre-registrada del filtro de noticias (docs/validacion/filtro_noticias_plan.md).
# Compara las operaciones abiertas durante NFP/FOMC con el resto, en 60 configuraciones
# sobre el historico largo de Dukascopy. Requiere la app en modo agente (API 8766).
#
#   pwsh -File scripts\backtest\prueba_noticias.ps1
param(
  [string]$Salida = (Join-Path $PSScriptRoot '..\..\docs\validacion\filtro_noticias_2026-11-15'),
  [string]$Desde = '2003-05-01',
  [string]$Corte = '2024-04-01',
  [int]$MargenMin = 30
)
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')
$api = 'http://localhost:8766/api/heavy/validar'
$repo = Join-Path $PSScriptRoot '..\..'

# --- ventanas de eventos, ordenadas y sin solaparse ---
$ev = (Get-Content -Raw -Encoding utf8 (Join-Path $repo 'docs\validacion\eventos_eeuu_2003_2024.json') | ConvertFrom-Json).eventos |
  Sort-Object inicio_utc
$vIni = [long[]]::new($ev.Count); $vFin = [long[]]::new($ev.Count); $vTipo = [string[]]::new($ev.Count)
for ($i = 0; $i -lt $ev.Count; $i++) {
  $vIni[$i] = ([DateTimeOffset]::Parse($ev[$i].inicio_utc)).ToUnixTimeSeconds() - 60 * $MargenMin
  $vFin[$i] = ([DateTimeOffset]::Parse($ev[$i].fin_utc)).ToUnixTimeSeconds() + 60 * $MargenMin
  $vTipo[$i] = $ev[$i].tipo
}
# Devuelve el tipo del evento cuya ventana toca [e0, e1], o $null
function Expuesta([long]$e0, [long]$e1) {
  $k = [Array]::BinarySearch($vIni, $e1)
  if ($k -lt 0) { $k = (-bnot $k) - 1 } else { $k = $k - 1 }   # ultima ventana que empieza antes de e1
  if ($k -ge 0 -and $vFin[$k] -gt $e0) { return $vTipo[$k] }
  return $null
}

# --- configuraciones: divisas/oro de la bateria larga + ideas 1-5 y 10-20 del lote 2 ---
$conDolar = @('frxEURUSD', 'frxGBPUSD', 'frxUSDJPY', 'frxUSDCHF', 'frxXAUUSD')
$configs = [System.Collections.Generic.List[object]]::new()
foreach ($c in @(
    @('BollingerRsiReversal_d30', 'frxXAUUSD', '1h', 50, 2), @('BollingerRsiReversal', 'frxEURUSD', '4h', 50, 2),
    @('IndicePullbackLong_v1', 'frxUSDJPY', '1h', 50, 2), @('IndicePullbackLong_v1', 'frxGBPUSD', '1h', 50, 2),
    @('EmaRsiCruce_v4', 'frxUSDJPY', '1h', 55, 2), @('EmaRsiCruce_v4', 'frxGBPUSD', '1h', 55, 2),
    @('LondresMomentum_v1', 'frxEURUSD', '1h', 50, 2), @('LondresMomentum_v1', 'frxGBPUSD', '1h', 50, 2),
    @('AsiaReversion_v1', 'frxEURUSD', '1h', 50, 2), @('AsiaReversion_v1', 'frxGBPUSD', '1h', 50, 2),
    @('SobrerreaccionVela_v1', 'frxEURUSD', '1h', 50, 2), @('SobrerreaccionVela_v1', 'frxGBPUSD', '1h', 50, 2),
    @('SobrerreaccionVela_v1', 'frxUSDJPY', '1h', 50, 2), @('SobrerreaccionVela_v1', 'frxXAUUSD', '1h', 50, 2))) {
  $configs.Add([pscustomobject]@{ est = $c[0]; sym = $c[1]; tf = $c[2]; conf = $c[3]; dur = $c[4] })
}
$lote2 = Get-Content -Raw -Encoding utf8 (Join-Path $repo 'docs\validacion\lote2_hipotesis.json') | ConvertFrom-Json
foreach ($p in $lote2.pruebas) {
  if ($p.tf -notin @('1h', '4h')) { continue }
  foreach ($sym in $p.mercados) {
    if ($sym -in $conDolar) { $configs.Add([pscustomobject]@{ est = $p.estrategia; sym = $sym; tf = $p.tf; conf = 50; dur = $p.dur }) }
  }
}

function Z2([double]$g1, [double]$n1, [double]$g2, [double]$n2) {
  if ($n1 -le 0 -or $n2 -le 0) { return 0.0 }
  $p = ($g1 + $g2) / ($n1 + $n2)
  return (($g1 / $n1) - ($g2 / $n2)) / [math]::Sqrt($p * (1 - $p) * (1 / $n1 + 1 / $n2))
}
function ColaBinomial([int]$k, [int]$n) {   # P(X >= k), X ~ Bin(n, 0.5)
  $s = 0.0; $c = 1.0
  for ($i = 0; $i -le $n; $i++) { if ($i -ge $k) { $s += $c }; $c = $c * ($n - $i) / ($i + 1) }
  return $s / [math]::Pow(2, $n)
}
function Pct([System.Collections.Generic.List[double]]$l, [double]$q) {
  if ($l.Count -eq 0) { return 0.0 }
  $a = $l.ToArray(); [Array]::Sort($a); return $a[[math]::Min($a.Count - 1, [int]($q * $a.Count))]
}

$tot = @{ nE = 0; gE = 0; nN = 0; gN = 0; nNFP = 0; gNFP = 0; nFOMC = 0; gFOMC = 0 }
$movE = [System.Collections.Generic.List[double]]::new(); $movN = [System.Collections.Generic.List[double]]::new()
$porMercado = @{}
$filas = @(); $resultado = @()
$i = 0
foreach ($c in $configs) {
  $i++
  $b = @{ estrategia = $c.est; symbol = $c.sym; timeframe = $c.tf; duracion = $c.dur; confianza = $c.conf; payout = 85
    corte = $Corte; desde = $Desde; fuente = 'Dukascopy'; incluir_operaciones = $true }
  try { $r = Invoke-RestMethod $api -Method POST -Body ($b | ConvertTo-Json -Compress) -ContentType 'application/json' -TimeoutSec 1800 }
  catch { $r = [pscustomobject]@{ ok = $false; error = $_.Exception.Message } }
  if (-not $r.ok) { $filas += ("{0,-26} {1,-10} {2} ERROR {3}" -f $c.est, $c.sym, $c.tf, $r.error); continue }
  if (-not $porMercado.ContainsKey($c.sym)) {
    $porMercado[$c.sym] = @{ e = [System.Collections.Generic.List[double]]::new(); n = [System.Collections.Generic.List[double]]::new() }
  }
  $cE = 0; $cGE = 0; $cN = 0; $cGN = 0
  $limite = ([DateTimeOffset]::Parse($Corte + 'T00:00:00Z')).ToUnixTimeSeconds()
  foreach ($o in $r.operaciones) {
    $e0 = [long]$o[0]; $e1 = [long]$o[1]; $g = [int]$o[3]
    if ($e0 -ge $limite) { continue }   # solo el periodo limpio
    $mov = if ([double]$o[4] -gt 0) { [math]::Abs([double]$o[5] / [double]$o[4] - 1) } else { 0.0 }
    $t = Expuesta $e0 $e1
    if ($t) {
      $cE++; $cGE += $g; $movE.Add($mov); $porMercado[$c.sym].e.Add($mov)
      if ($t -eq 'NFP') { $tot.nNFP++; $tot.gNFP += $g } else { $tot.nFOMC++; $tot.gFOMC += $g }
    } else {
      $cN++; $cGN += $g; $movN.Add($mov); $porMercado[$c.sym].n.Add($mov)
    }
  }
  $tot.nE += $cE; $tot.gE += $cGE; $tot.nN += $cN; $tot.gN += $cGN
  $wE = if ($cE) { $cGE / $cE } else { 0 }; $wN = if ($cN) { $cGN / $cN } else { 0 }
  $filas += ("{0,-26} {1,-10} {2} expuestas {3,5} ({4,5:N1}%) | resto {5,6} ({6,5:N1}%) | dif {7,6:N1} pts" -f `
      $c.est, $c.sym, $c.tf, $cE, ($wE * 100), $cN, ($wN * 100), (($wE - $wN) * 100))
  $resultado += [pscustomobject]@{ estrategia = $c.est; symbol = $c.sym; tf = $c.tf; dur = $c.dur
    expuestas = $cE; ganadas_expuestas = $cGE; resto = $cN; ganadas_resto = $cGN }
  Write-Output ("{0,2}/{1} {2} {3} {4}: expuestas {5} ({6:N1}%) resto {7} ({8:N1}%)" -f $i, $configs.Count, $c.est, $c.sym, $c.tf, $cE, ($wE * 100), $cN, ($wN * 100))
}

# --- resumen ---
$wE = $tot.gE / [math]::Max(1, $tot.nE); $wN = $tot.gN / [math]::Max(1, $tot.nN)
$wTodo = ($tot.gE + $tot.gN) / [math]::Max(1, $tot.nE + $tot.nN)
$z = Z2 $tot.gE $tot.nE $tot.gN $tot.nN
$validas = @($resultado | Where-Object { $_.expuestas -ge 20 })
$peores = @($validas | Where-Object { ($_.ganadas_expuestas / $_.expuestas) -lt ($_.ganadas_resto / [math]::Max(1, $_.resto)) }).Count
$empates = @($validas | Where-Object { ($_.ganadas_expuestas / $_.expuestas) -eq ($_.ganadas_resto / [math]::Max(1, $_.resto)) }).Count
$m = $validas.Count - $empates
$pSignos = ColaBinomial $peores $m
$filtroBinarias = ($z -le -2.0) -and ($pSignos -lt 0.05)
$razonMov = (($movE | Measure-Object -Average).Average) / [math]::Max(1e-12, (($movN | Measure-Object -Average).Average))

$lineas = @('PRUEBA DEL FILTRO DE NOTICIAS - plan pre-registrado en docs/validacion/filtro_noticias_plan.md',
  ("Eventos: {0} (NFP y FOMC 2003-2024), ventana +-{1} min. Dukascopy {2} a {3}. {4} configuraciones." -f $ev.Count, $MargenMin, $Desde, $Corte, $configs.Count),
  '',
  '1) BINARIAS: acierto de las operaciones abiertas durante una noticia frente al resto',
  ("   Expuestas: {0} ops, acierto {1:N2}%   |   Resto: {2} ops, acierto {3:N2}%" -f $tot.nE, ($wE * 100), $tot.nN, ($wN * 100)),
  ("   Diferencia {0:N2} pts, z {1:N2}. Configuraciones con >= 20 expuestas: {2}; peor en {3} de {4} (empates {5}), p signos {6:N4}" -f `
      (($wE - $wN) * 100), $z, $validas.Count, $peores, $m, $empates, $pSignos),
  ("   Si se filtraran: se pierden {0:N1}% de las operaciones y el acierto total pasa de {1:N2}% a {2:N2}% ({3:+0.00;-0.00} pts)" -f `
      ($tot.nE / [math]::Max(1, $tot.nE + $tot.nN) * 100), ($wTodo * 100), ($wN * 100), (($wN - $wTodo) * 100)),
  ("   Veredicto binarias: {0}" -f $(if ($filtroBinarias) { 'FILTRO RECOMENDADO (z <= -2 y prueba de signos p < 0,05)' } else { 'SIN EFECTO SUFICIENTE: no se recomienda el filtro para binarias' })),
  ("   Por evento: NFP {0} ops {1:N2}% | FOMC {2} ops {3:N2}%" -f $tot.nNFP, ($tot.gNFP / [math]::Max(1, $tot.nNFP) * 100), $tot.nFOMC, ($tot.gFOMC / [math]::Max(1, $tot.nFOMC) * 100)),
  '',
  '2) MULTIPLICADORES: movimiento absoluto del precio durante la operacion',
  ("   Expuestas: media {0:N3}%, p95 {1:N3}%   |   Resto: media {2:N3}%, p95 {3:N3}%   |   razon de medias x{4:N2}" -f `
      (($movE | Measure-Object -Average).Average * 100), ((Pct $movE 0.95) * 100), (($movN | Measure-Object -Average).Average * 100), ((Pct $movN 0.95) * 100), $razonMov))
foreach ($s in ($porMercado.Keys | Sort-Object)) {
  $a = $porMercado[$s]
  if ($a.e.Count -eq 0 -or $a.n.Count -eq 0) { continue }
  $me = ($a.e | Measure-Object -Average).Average; $mn = ($a.n | Measure-Object -Average).Average
  $lineas += ("   {0,-10} expuestas {1,6} media {2:N3}% p95 {3:N3}% | resto {4,7} media {5:N3}% p95 {6:N3}% | x{7:N2}" -f `
      $s, $a.e.Count, ($me * 100), ((Pct $a.e 0.95) * 100), $a.n.Count, ($mn * 100), ((Pct $a.n 0.95) * 100), ($me / [math]::Max(1e-12, $mn)))
}
$lineas += ("   Veredicto multiplicadores: {0}" -f $(if ($razonMov -ge 1.5) { 'FILTRO RECOMENDADO (las expuestas se mueven >= 1,5 veces mas)' } else { 'diferencia de movimiento menor a 1,5 veces' }))
$lineas += ''; $lineas += 'DETALLE POR CONFIGURACION:'; $lineas += $filas
$lineas | Set-Content -Encoding utf8 ($Salida + '.txt')
[pscustomobject]@{ eventos = $ev.Count; margen_min = $MargenMin; total = $tot; z = $z; p_signos = $pSignos; razon_movimiento = $razonMov
  configuraciones = $resultado } | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 ($Salida + '.json')
Write-Output ''
$lineas[0..17] | Write-Output
