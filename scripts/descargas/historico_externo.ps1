# Historico largo externo, guardado en la tabla candles con platform = fuente (nunca se
# mezcla con las velas de Deriv):
#   Dukascopy  divisas, metales e indices OTC desde 2003 (US100 2011, DAX 2013); 1m a 1d
#   Bitstamp   cripto contra USD (cryBTCUSD) desde 2011; 1h, 4h, 1d
#   Binance    cripto contra USDT (cryBTCUSD -> BTCUSDT) desde 2017; 1h, 4h, 1d
#   Yahoo      indices de contado (OTC_NDX, OTC_GDAXI...) desde 2002; solo 1d
# Lo descarga la propia app (API 8766, modo agente); el mantenimiento nocturno del modo
# autonomo pone al dia todas las series guardadas. Lo ya guardado no se vuelve a bajar: si
# la fuente falla, basta con volver a lanzar la descarga.
#
#   pwsh -File scripts\descargas\historico_externo.ps1 descargar -Simbolos frxEURUSD,frxGBPUSD -Desde 2003-05-01
#   pwsh -File scripts\descargas\historico_externo.ps1 descargar -Fuente Bitstamp -Simbolos cryBTCUSD
#   pwsh -File scripts\descargas\historico_externo.ps1 descargar -Fuente Yahoo -Simbolos OTC_NDX,OTC_GDAXI -Temporalidades 1d
#   pwsh -File scripts\descargas\historico_externo.ps1 descargar -Simbolos frxEURUSD -Temporalidades 5m -Desde 2020-01-01
#   pwsh -File scripts\descargas\historico_externo.ps1 estado
#   pwsh -File scripts\descargas\historico_externo.ps1 detener
#   pwsh -File scripts\descargas\historico_externo.ps1 cobertura
#   pwsh -File scripts\descargas\historico_externo.ps1 comparar -Simbolos frxEURUSD -Temporalidades 1h
#   pwsh -File scripts\descargas\historico_externo.ps1 comparar -Fuente Yahoo -Simbolos OTC_NDX -Temporalidades 1d
#
# Temporalidades: 1h, 4h y 1d salen de un archivo por mes (rapido); 1m, 5m, 15m y 30m de un
# archivo por dia (lento: unos 6000 archivos por simbolo en 20 anos).
param(
  [Parameter(Position = 0)][ValidateSet('descargar', 'estado', 'detener', 'cobertura', 'comparar')][string]$Accion = 'estado',
  [ValidateSet('Dukascopy', 'Bitstamp', 'Binance', 'Yahoo')][string]$Fuente = 'Dukascopy',
  [string[]]$Simbolos = @('frxEURUSD', 'frxGBPUSD', 'frxUSDJPY', 'frxXAUUSD', 'frxGBPJPY', 'frxUSDCHF', 'frxXAGUSD'),
  [string[]]$Temporalidades = @('1h', '4h', '1d'),
  [string]$Desde = '',
  [string]$Hasta = ''
)
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')
$api = 'http://localhost:8766/api/heavy/historico-externo'

switch ($Accion) {
  'descargar' {
    # Valores por defecto segun la fuente (los de arriba son los de Dukascopy)
    if (-not $PSBoundParameters.ContainsKey('Simbolos')) {
      switch ($Fuente) { 'Bitstamp' { $Simbolos = @('cryBTCUSD') } 'Binance' { $Simbolos = @('cryBTCUSD') } 'Yahoo' { $Simbolos = @('OTC_NDX', 'OTC_GDAXI') } }
    }
    if ($Fuente -eq 'Yahoo' -and -not $PSBoundParameters.ContainsKey('Temporalidades')) { $Temporalidades = @('1d') }
    $body = @{ fuente = $Fuente; simbolos = $Simbolos; temporalidades = $Temporalidades }
    if ($Desde) { $body.desde = $Desde }
    if ($Hasta) { $body.hasta = $Hasta }
    $r = Invoke-RestMethod "$api/descargar" -Method POST -Body ($body | ConvertTo-Json -Compress) -ContentType 'application/json' -TimeoutSec 60
    if (-not $r.ok) { Write-Output ("ERROR: {0}" -f $r.error); exit 1 }
    Write-Output $r.mensaje
  }
  'estado' {
    $e = (Invoke-RestMethod "$api/estado?fuente=$Fuente" -TimeoutSec 30).estado
    Write-Output ("Activa: {0}  |  {1}" -f $e.activo, $e.mensaje)
    if ($e.inicio_utc) {
      Write-Output ("Inicio: {0}  Archivos bajados: {1}  sin datos: {2}  ya guardados: {3}  Velas guardadas: {4}" -f `
        $e.inicio_utc, $e.archivos_bajados, $e.archivos_sin_datos, $e.archivos_omitidos_ya_guardados, $e.velas_guardadas)
    }
    if ($e.por_serie) { $e.por_serie.PSObject.Properties | ForEach-Object { Write-Output ("  {0,-20} {1,8} velas" -f $_.Name, $_.Value) } }
    if ($e.archivos_fallidos -and $e.archivos_fallidos.Count -gt 0) {
      Write-Output ("Archivos que no se pudieron bajar ({0}); se completan volviendo a lanzar la descarga:" -f $e.archivos_fallidos.Count)
      $e.archivos_fallidos | Select-Object -First 20 | ForEach-Object { Write-Output "  $_" }
    }
  }
  'detener' {
    (Invoke-RestMethod "$api/detener?fuente=$Fuente" -Method POST -TimeoutSec 30).mensaje
  }
  'cobertura' {
    $r = Invoke-RestMethod "$api/cobertura" -TimeoutSec 120
    foreach ($f in 'deriv', 'dukascopy', 'bitstamp', 'binance', 'yahoo') {
      Write-Output ("== {0} ==" -f $f.ToUpper())
      foreach ($c in $r.$f) { Write-Output ("  {0,-12} {1,-4} {2,9} velas  {3} -> {4}" -f $c.symbol, $c.timeframe, $c.velas, $c.desde, $c.hasta) }
    }
  }
  'comparar' {
    foreach ($s in $Simbolos) {
      foreach ($tf in $Temporalidades) {
        $c = Invoke-RestMethod ("$api/comparar?symbol={0}&timeframe={1}&fuente={2}" -f $s, $tf, $Fuente) -TimeoutSec 120
        Write-Output ("{0,-12} {1,-4} velas comparadas {2,7} ({3} -> {4})  misma direccion {5,6}%  diferencia media de cierre {6}%" -f `
          $s, $tf, $c.velas_comparadas, $c.desde, $c.hasta, $c.misma_direccion_pct, $c.diferencia_media_cierre_pct)
      }
    }
  }
}
