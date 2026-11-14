# Forward test en la cuenta DEMO de las dos pistas del multimercado
# (frxUSDJPY 1h y frxGBPUSD 1h). Plan pre-registrado: docs/validacion/forward_test_plan.md
#
# Uso (con la app en modo agente: WpfDerivClientVB.exe --heavy-api --minimized):
#   pwsh -File scripts\live\forward_test.ps1 -Accion iniciar    # una sola vez
#   pwsh -File scripts\live\forward_test.ps1 -Accion estado     # cuando quieras
#   pwsh -File scripts\live\forward_test.ps1 -Accion reanudar   # tras reiniciar la app o el PC
#   pwsh -File scripts\live\forward_test.ps1 -Accion detener    # al terminar
#
# Solo cuenta DEMO. Monto fijo de 1 USD. No cambiar reglas ni parametros durante la prueba.
param([ValidateSet('iniciar','estado','reanudar','detener')][string]$Accion = 'estado')

. (Join-Path $PSScriptRoot '..\agent_auth.ps1')
$api = 'http://localhost:8766/api/heavy'
$rutaSesiones = Join-Path $PSScriptRoot '..\..\docs\validacion\forward_test_sesiones.json'

# Pre-registrado: no modificar mientras dure la prueba
$plan = @(
  @{ estrategia='IndicePullbackLong_v1'; symbol='frxUSDJPY'; timeframe='1h'; confianza=50; duracion=2 },
  @{ estrategia='IndicePullbackLong_v1'; symbol='frxGBPUSD'; timeframe='1h'; confianza=50; duracion=2 },
  @{ estrategia='EmaRsiCruce_v4';        symbol='frxUSDJPY'; timeframe='1h'; confianza=55; duracion=2 },
  @{ estrategia='EmaRsiCruce_v4';        symbol='frxGBPUSD'; timeframe='1h'; confianza=55; duracion=2 }
)

function Conectar-Demo {
  $e = Invoke-RestMethod "$api/trading/estado" -TimeoutSec 15
  if ($e.connected -and $e.demo) { return }
  if ($e.connected -and -not $e.demo) { throw 'La app esta conectada a la cuenta REAL. El forward test es solo en DEMO.' }
  $c = Invoke-RestMethod "$api/trading/conectar" -Method POST -Body '{"demo":true}' -ContentType 'application/json' -TimeoutSec 60
  if (-not $c.ok -or -not $c.connected) { throw ("No se pudo conectar la cuenta demo: {0}" -f $c.error) }
  Write-Output ("Conectado a la cuenta demo de {0}." -f $c.cliente)
}

function Wilson([int]$g, [int]$n) {
  if ($n -eq 0) { return @(0.0, 0.0) }
  $z = 1.96; $p = $g / $n
  $den = 1 + $z * $z / $n
  $centro = ($p + $z * $z / (2 * $n)) / $den
  $margen = $z * [Math]::Sqrt($p * (1 - $p) / $n + $z * $z / (4 * $n * $n)) / $den
  return @(($centro - $margen), ($centro + $margen))
}

switch ($Accion) {
  'iniciar' {
    if (Test-Path $rutaSesiones) { throw "Ya hay un forward test registrado ($rutaSesiones). Usa -Accion reanudar o estado." }
    Conectar-Demo
    $sesiones = @()
    foreach ($p in $plan) {
      $body = @{ estrategia=$p.estrategia; symbol=$p.symbol; timeframe=$p.timeframe; confianza=$p.confianza;
                 duracion=$p.duracion; payout=80; monto=1; autonomo=$true } | ConvertTo-Json -Compress
      $r = Invoke-RestMethod "$api/live/iniciar" -Method POST -Body $body -ContentType 'application/json' -TimeoutSec 60
      if (-not $r.ok) { Write-Output ("ERROR {0} {1}: {2}" -f $p.estrategia, $p.symbol, $r.error); continue }
      Write-Output ("Sesion {0}: {1} {2} {3}" -f $r.sesion_id, $p.estrategia, $p.symbol, $p.timeframe)
      $sesiones += [pscustomobject]@{ sesion_id=$r.sesion_id; estrategia=$p.estrategia; symbol=$p.symbol; timeframe=$p.timeframe;
                                      confianza=$p.confianza; duracion=$p.duracion }
    }
    [pscustomobject]@{ inicio_utc=(Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'); sesiones=$sesiones } |
      ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 $rutaSesiones
    Write-Output "Registrado en docs/validacion/forward_test_sesiones.json. La app debe quedar abierta y conectada."
  }
  'reanudar' {
    Conectar-Demo
    $r = Invoke-RestMethod "$api/live/reanudar-bd" -Method POST -Body '{}' -ContentType 'application/json' -TimeoutSec 60
    Write-Output ("Sesiones reanudadas: {0}" -f $r.reanudadas)
  }
  'detener' {
    $reg = Get-Content $rutaSesiones -Raw | ConvertFrom-Json
    foreach ($s in $reg.sesiones) {
      $r = Invoke-RestMethod "$api/live/detener" -Method POST -Body (@{ sesion_id=$s.sesion_id } | ConvertTo-Json -Compress) -ContentType 'application/json' -TimeoutSec 60
      Write-Output ("Sesion {0} detenida: {1}" -f $s.sesion_id, $r.ok)
    }
  }
  'estado' {
    if (-not (Test-Path $rutaSesiones)) { throw 'No hay forward test registrado. Usa -Accion iniciar.' }
    $reg = Get-Content $rutaSesiones -Raw | ConvertFrom-Json
    $ids = ($reg.sesiones | ForEach-Object { [int]$_.sesion_id }) -join ','
    $sql = "select sesion_id, estado, count(*), coalesce(sum(profit_loss),0), coalesce(avg(case when estado='Ganado' then profit_loss/nullif(monto,0) end),0) " +
           "from public.backtest_operaciones where sesion_id in ($ids) group by sesion_id, estado order by sesion_id, estado;"
    $filas = docker exec traiding_postgres psql -U traiding_user -d traiding_db -At -F '|' -c $sql
    $dias = [Math]::Round(((Get-Date).ToUniversalTime() - [datetime]::Parse($reg.inicio_utc).ToUniversalTime()).TotalDays, 1)
    Write-Output ("Forward test desde {0} ({1} dias)" -f $reg.inicio_utc, $dias)
    $tot = @{ g=0; p=0; otras=0; pnl=0.0; pagos=@() }
    foreach ($s in $reg.sesiones) {
      $g = 0; $p = 0; $otras = 0; $pnl = 0.0; $pago = 0.0
      foreach ($f in $filas) {
        $c = $f -split '\|'
        if ([int]$c[0] -ne [int]$s.sesion_id) { continue }
        $inv = [Globalization.CultureInfo]::InvariantCulture
        switch ($c[1]) {
          'Ganado'  { $g = [int]$c[2]; $pago = [double]::Parse($c[4], $inv) }
          'Perdido' { $p = [int]$c[2] }
          default   { $otras += [int]$c[2] }
        }
        $pnl += [double]::Parse($c[3], $inv)
      }
      $n = $g + $p
      $ic = Wilson $g $n
      Write-Output ("  #{0,-4} {1,-22} {2,-9} | {3,3} ops  {4,6:P1}  IC {5:P0}-{6:P0} | pnl {7,7:N2} | payout real {8:P1} | sin ejecutar/pendientes {9}" -f `
        $s.sesion_id, $s.estrategia, $s.symbol, $n, $(if ($n) { $g / $n } else { 0 }), $ic[0], $ic[1], $pnl, $pago, $otras)
      $tot.g += $g; $tot.p += $p; $tot.otras += $otras; $tot.pnl += $pnl
      if ($g -gt 0) { $tot.pagos += ,@($pago, $g) }
    }
    $n = $tot.g + $tot.p
    $pagoMedio = 0.0
    if ($tot.pagos.Count) { $pagoMedio = ($tot.pagos | ForEach-Object { $_[0] * $_[1] } | Measure-Object -Sum).Sum / $tot.g }
    $eq = if ($pagoMedio -gt 0) { 1 / (1 + $pagoMedio) } else { 0.5405 }
    $ic = Wilson $tot.g $n
    Write-Output ("  TOTAL: {0} ops {1:P1} IC95 {2:P1}-{3:P1} | pnl {4:N2} USD | minimo para no perder {5:P1} (payout real {6:P1})" -f `
      $n, $(if ($n) { $tot.g / $n } else { 0 }), $ic[0], $ic[1], $tot.pnl, $eq, $pagoMedio)
    if ($n -lt 300) {
      Write-Output ("  Veredicto: todavia no (criterio: 300 operaciones o 6 meses; van {0})." -f $n)
    } elseif ($ic[0] -gt $eq) {
      Write-Output '  Veredicto: VENTAJA (el limite inferior del IC supera el minimo para no perder).'
    } else {
      Write-Output '  Veredicto: SIN VENTAJA DEMOSTRADA.'
    }
    if ($n -ge 150 -and $ic[1] -lt $eq) { Write-Output '  Corte anticipado: el limite SUPERIOR del IC ya esta por debajo del minimo -> detener, no hay ventaja.' }
  }
}
