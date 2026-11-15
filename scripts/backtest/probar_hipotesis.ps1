# Embudo para ideas nuevas: registra la hipotesis con su motivo ANTES de probarla, corre la
# validacion completa (prueba ciega, walk-forward, Monte Carlo, ruido, otros mercados) y corrige
# el veredicto por el numero de hipotesis probadas hasta hoy.
# Requiere la app en modo agente (API 8766). Sin -Payout usa el payout real medido (o 85 si no hay).
#
#   pwsh -File scripts\backtest\probar_hipotesis.ps1 -Estrategia EmaRsiCruce_v4 -Symbol frxUSDJPY -Timeframe 1h `
#        -Duracion 2 -Confianza 55 -Motivo "En la sesion de Tokio el yen tiende a revertir tras excesos de RSI"
#   pwsh -File scripts\backtest\probar_hipotesis.ps1 -Listar
param(
  [string]$Estrategia, [string]$Symbol, [string]$Timeframe = '1h',
  [int]$Duracion = 2, [int]$Confianza = 55, [string]$Motivo,
  [double]$Payout = 0, [string]$Corte = '2026-04-29',
  [switch]$Listar
)
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')
$api = 'http://localhost:8766/api/heavy/hipotesis'

if ($Listar) {
  $r = Invoke-RestMethod $api -TimeoutSec 30
  foreach ($h in $r.hipotesis) {
    Write-Output ("#{0,-3} {1} {2,-22} {3,-10} {4,-3} conf {5} dur {6} | ciego: {7,-27} corregido: {8,-27} | {9}" -f `
      $h.numero_prueba, ([datetime]$h.creada_utc).ToUniversalTime().ToString('yyyy-MM-dd'), $h.estrategia, $h.symbol, $h.timeframe, $h.confianza, $h.duracion,
      $h.veredicto_ciego, $h.veredicto_corregido, $h.motivo)
  }
  exit 0
}
if (-not $Estrategia -or -not $Symbol -or -not $Motivo) { throw 'Faltan -Estrategia, -Symbol y -Motivo (por que deberia funcionar).' }
$body = @{ estrategia=$Estrategia; symbol=$Symbol; timeframe=$Timeframe; duracion=$Duracion; confianza=$Confianza; corte=$Corte; motivo=$Motivo }
if ($Payout -gt 0) { $body.payout = $Payout }
$r = Invoke-RestMethod $api -Method POST -Body ($body | ConvertTo-Json -Compress) -ContentType 'application/json' -TimeoutSec 3600
if (-not $r.ok) { Write-Output ("ERROR: {0}" -f $r.error); exit 1 }
Write-Output $r.informe
