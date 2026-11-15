# Prueba con historico largo (Dukascopy 2003-2024)

Plan **pre-registrado** el 2026-11-15, antes de correr la prueba. Las reglas no se tocan
despues de ver los resultados.

## Por que esta prueba es limpia

Todas las estrategias y parametros se disenaron mirando velas de Deriv de 2024-04 a
2026-11. Las velas de **2003-05-01 a 2024-03-31** (Dukascopy) nunca se usaron para
disenar nada: son 21 anos fuera de muestra. Ahi se mide si la ventaja existe.

## Que se prueba (fijo)

| # | Estrategia | Mercado | Conf | Dur | Origen |
|---|---|---|---|---|---|
| 1 | BollingerMomentum_v2 | OTC_NDX 1h | 50 | 2 | top validacion 2026-11-14 |
| 2 | BollingerMomentum_v2 | OTC_NDX 1h | 60 | 2 | top validacion |
| 3 | IndicePullbackLong_v1 | OTC_NDX 4h | 50 | 2 | top validacion |
| 4 | IndicePullbackLong_v1 | OTC_DJI 4h | 50 | 2 | top validacion |
| 5 | IndicePullbackLong_v1 | OTC_SPC 4h | 50 | 2 | top validacion |
| 6 | CCIReversal_v2 | OTC_DJI 4h | 55 | 2 | top validacion |
| 7 | EmaRsiCruce_v4 | OTC_NDX 4h | 55 | 2 | top validacion |
| 8 | BollingerRsiReversal_d30 | frxXAUUSD 1h | 50 | 2 | top validacion |
| 9 | BollingerRsiReversal | frxEURUSD 4h | 50 | 2 | top validacion |
| 10 | IndicePullbackLong_v1 | frxUSDJPY 1h | 50 | 2 | forward test |
| 11 | IndicePullbackLong_v1 | frxGBPUSD 1h | 50 | 2 | forward test |
| 12 | EmaRsiCruce_v4 | frxUSDJPY 1h | 55 | 2 | forward test |
| 13 | EmaRsiCruce_v4 | frxGBPUSD 1h | 55 | 2 | forward test |
| 14 | LondresMomentum_v1 | frxEURUSD 1h | 50 | 2 | hipotesis 2026-11-15 |
| 15 | LondresMomentum_v1 | frxGBPUSD 1h | 50 | 2 | hipotesis |
| 16 | AsiaReversion_v1 | frxEURUSD 1h | 50 | 2 | hipotesis |
| 17 | AsiaReversion_v1 | frxGBPUSD 1h | 50 | 2 | hipotesis |
| 18 | SobrerreaccionVela_v1 | frxEURUSD 1h | 50 | 2 | hipotesis |
| 19 | SobrerreaccionVela_v1 | frxGBPUSD 1h | 50 | 2 | hipotesis |
| 20 | SobrerreaccionVela_v1 | frxUSDJPY 1h | 50 | 2 | hipotesis |
| 21 | SobrerreaccionVela_v1 | frxXAUUSD 1h | 50 | 2 | hipotesis |

Los dos de R_100 de la validacion anterior no entran: R_100 solo existe en Deriv.

## Como se mide

- Fuente Dukascopy, desde 2003-05-01, corte 2024-04-01: el periodo **antes** del corte
  es la prueba limpia (21 anos). Una posicion a la vez, entrada al cierre de la vela.
- Payout supuesto 85% (equilibrio 54,05%). Es optimista: el real de divisas ronda 75-79%
  (equilibrio 55,9-57,1%). Tambien se informa el resultado con 78%.
- Correccion por pruebas multiples: 21 pruebas, Bonferroni (alfa 0,05/21, z = 2,82
  unilateral). Solo cuenta como **ventaja** si el acierto supera el equilibrio con esa z.
- Control de la fuente: misma configuracion en 2024-04-01 a hoy con Deriv y con Dukascopy.
  Si los aciertos difieren mucho, la fuente externa no sirve para esa estrategia.

## Que decide

- Ventaja corregida con payout 85% y positiva con 78% -> candidata a forward test en demo.
- Cualquier otra cosa -> sin ventaja; no se ajustan parametros sobre estos datos.
