# Historico largo externo (Dukascopy): divisas, metales e indices desde 2003, guardado en la
# tabla candles con platform = 'Dukascopy' (nunca se mezcla con las velas de Deriv).
# Lo descarga la propia app (API 8766, modo agente); el mantenimiento nocturno del modo
# autonomo lo pone al dia solo. Lo ya guardado no se vuelve a bajar: si el servidor de
# Dukascopy falla, basta con volver a lanzar la descarga.
#
#   pwsh -File scripts\descargas\historico_externo.ps1 descargar -Simbolos frxEURUSD,frxGBPUSD -Desde 2003-05-01
#   pwsh -File scripts\descargas\historico_externo.ps1 descargar -Simbolos frxEURUSD -Temporalidades 5m -Desde 2020-01-01
#   pwsh -File scripts\descargas\historico_externo.ps1 estado
#   pwsh -File scripts\descargas\historico_externo.ps1 detener
#   pwsh -File scripts\descargas\historico_externo.ps1 cobertura
#   pwsh -File scripts\descargas\historico_externo.ps1 comparar -Simbolos frxEURUSD -Temporalidades 1h
#
# Temporalidades: 1h, 4h y 1d salen de un archivo por mes (rapido); 1m, 5m, 15m y 30m de un
# archivo por dia (lento: unos 6000 archivos por simbolo en 20 anos).
param(
  [Parameter(Position = 0)][ValidateSet('descargar', 'estado', 'detener', 'cobertura', 'comparar')][string]$Accion = 'estado',
  [string[]]$Simbolos = @('frxEURUSD', 'frxGBPUSD', 'frxUSDJPY', 'frxXAUUSD', 'frxGBPJPY', 'frxUSDCHF', 'frxXAGUSD'),
  [string[]]$Temporalidades = @('1h', '4h', '1d'),
  [string]$Desde = '2003-05-01',
  [string]$Hasta = ''
)
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')
$api = 'http://localhost:8766/api/heavy/historico-externo'

switch ($Accion) {
  'descargar' {
    $body = @{ simbolos = $Simbolos; temporalidades = $Temporalidades; desde = $Desde }
    if ($Hasta) { $body.hasta = $Hasta }
    $r = Invoke-RestMethod "$api/descargar" -Method POST -Body ($body | ConvertTo-Json -Compress) -ContentType 'application/json' -TimeoutSec 60
    if (-not $r.ok) { Write-Output ("ERROR: {0}" -f $r.error); exit 1 }
    Write-Output $r.mensaje
  }
  'estado' {
    $e = (Invoke-RestMethod "$api/estado" -TimeoutSec 30).estado
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
    (Invoke-RestMethod "$api/detener" -Method POST -TimeoutSec 30).mensaje
  }
  'cobertura' {
    $r = Invoke-RestMethod "$api/cobertura" -TimeoutSec 120
    foreach ($f in 'deriv', 'dukascopy') {
      Write-Output ("== {0} ==" -f $f.ToUpper())
      foreach ($c in $r.$f) { Write-Output ("  {0,-12} {1,-4} {2,9} velas  {3} -> {4}" -f $c.symbol, $c.timeframe, $c.velas, $c.desde, $c.hasta) }
    }
  }
  'comparar' {
    foreach ($s in $Simbolos) {
      foreach ($tf in $Temporalidades) {
        $c = Invoke-RestMethod ("$api/comparar?symbol={0}&timeframe={1}" -f $s, $tf) -TimeoutSec 120
        Write-Output ("{0,-12} {1,-4} velas comparadas {2,7} ({3} -> {4})  misma direccion {5,6}%  diferencia media de cierre {6}%" -f `
          $s, $tf, $c.velas_comparadas, $c.desde, $c.hasta, $c.misma_direccion_pct, $c.diferencia_media_cierre_pct)
      }
    }
  }
}
