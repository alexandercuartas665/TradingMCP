# Valida fuera de muestra las mejores configuraciones historicas.
# Corte = 2026-04-29 (fin de los datos con los que se disenaron las estrategias).
. 'C:\DesarrolloIA\Traiding\scripts\agent_auth.ps1'
$casos = @(
  @('BollingerMomentum_v2','OTC_NDX','1h',2,50,85),
  @('BollingerMomentum_v2','OTC_NDX','1h',2,60,85),
  @('IndicePullbackLong_v1','OTC_NDX','4h',2,50,85),
  @('IndicePullbackLong_v1','OTC_DJI','4h',2,50,85),
  @('IndicePullbackLong_v1','OTC_SPC','4h',2,50,85),
  @('CCIReversal_v2','OTC_DJI','4h',2,55,90),
  @('EmaRsiCruce_v4','OTC_NDX','4h',2,55,85),
  @('BollingerRsiReversal_d30','frxXAUUSD','1h',2,50,85),
  @('BollingerRsiReversal','frxEURUSD','4h',2,50,85),
  @('BollingerMomentum_v2','R_100','1h',2,50,85),
  @('EmaRsiCruce_v4','R_100','4h',2,55,85)
)
$filas = foreach ($c in $casos) {
  $body = @{ estrategia=$c[0]; symbol=$c[1]; timeframe=$c[2]; duracion=$c[3]; confianza=$c[4]; payout=$c[5]; corte='2026-04-29' } | ConvertTo-Json -Compress
  $r = Invoke-RestMethod -Uri 'http://localhost:8766/api/heavy/validar' -Method POST -Body $body -ContentType 'application/json' -TimeoutSec 300
  if (-not $r.ok) { [pscustomobject]@{ Estrategia=$c[0]; Activo="$($c[1]) $($c[2])"; Error=$r.error }; continue }
  $a = $r.periodo_diseno; $b = $r.periodo_ciego
  [pscustomobject]@{
    Estrategia = $c[0]; Activo = "$($c[1]) $($c[2])"; Dur = $c[3]; Conf = $c[4]; Pay = $c[5]
    Equilibrio = [math]::Round($r.punto_equilibrio * 100, 1)
    Dis_Ops = $a.operaciones; Dis_Win = [math]::Round($a.win_rate * 100, 1)
    Dis_IC = "{0:N1}-{1:N1}" -f ($a.ic95_inf*100), ($a.ic95_sup*100); Dis_Pnl = $a.pnl_por_dolar
    Ciego_Ops = $b.operaciones; Ciego_Win = [math]::Round($b.win_rate * 100, 1)
    Ciego_IC = "{0:N1}-{1:N1}" -f ($b.ic95_inf*100), ($b.ic95_sup*100); Ciego_Pnl = $b.pnl_por_dolar
    Veredicto_Ciego = $b.veredicto; Huecos = $r.descartadas_por_hueco
    Ciego_Desde = $b.desde; Ciego_Hasta = $b.hasta
  }
}
$filas | ConvertTo-Json -Depth 3 | Set-Content -Encoding utf8 (Join-Path $PSScriptRoot '....docsalidacionalidacion_resultados.json')
$filas | Format-Table Estrategia, Activo, Equilibrio, Dis_Ops, Dis_Win, Dis_IC, Ciego_Ops, Ciego_Win, Ciego_IC, Ciego_Pnl, Veredicto_Ciego -AutoSize | Out-String -Width 250
