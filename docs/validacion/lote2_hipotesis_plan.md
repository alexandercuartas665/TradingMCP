# Lote 2: 20 ideas nuevas con historico largo

Plan **pre-registrado** el 2026-11-15, antes de correr ninguna prueba. Las ideas, los
mercados y la regla de decision estan fijados en `lote2_hipotesis.json` y en los JSON de
cada estrategia (descripcion con el motivo). No se ajusta ningun parametro despues.

## Las ideas

Se buscaron ideas de otro tipo que las de indicadores ya probadas: horarios con flujos
conocidos, huecos, patrones de vela, rupturas y tendencia en diario. Periodos estandar
(ATR14, Bollinger 20/2, EMA50, Donchian 20, Ichimoku 9/26/52), sin optimizar.

| # | Idea | Familia | Mercados | TF | Dur |
|---|---|---|---|---|---|
| 1 | TokioFixYen: PUT tras el fix de Tokio | horario | USD/JPY, GBP/JPY | 1h | 2 |
| 2 | LunesGap: se cierra el hueco de apertura semanal | hueco | EUR, GBP, JPY, oro | 1h | 2 |
| 3 | FixLondresReversion: se deshace el movimiento hacia el fix | horario | EUR, GBP | 1h | 2 |
| 4 | NYDatosMomentum: vela fuerte en datos de EE. UU. continua | horario | EUR, GBP, JPY | 1h | 2 |
| 5 | ViernesTarde: cierre de posiciones antes del fin de semana | horario | EUR, GBP | 1h | 2 |
| 6 | RSI2Indices: retroceso corto en tendencia (Connors) | reversion | S&P, Nasdaq | 1d | 2 |
| 7 | RupturaDonchianDiaria: nuevo maximo/minimo de 20 dias | ruptura | EUR, GBP, JPY, oro | 1d | 1 |
| 8 | MomentumSerieTemporal: la tendencia diaria sigue | tendencia | EUR, GBP, JPY, oro | 1d | 1 |
| 9 | ReversionDiariaExtrema: dia de rango > 2 ATR corrige | reversion | EUR, GBP, JPY | 1d | 1 |
| 10 | InsideBarTendencia: compresion a favor de la tendencia | patron | EUR, GBP, JPY | 4h | 2 |
| 11 | EnvolventeBanda: envolvente que rechaza la banda | patron | EUR, GBP, JPY, oro | 4h | 2 |
| 12 | MartilloBanda: martillo/estrella en la banda | patron | EUR, GBP, JPY | 1h | 2 |
| 13 | CompresionRuptura: ruptura tras volatilidad baja | volatilidad | EUR, GBP, JPY | 1h | 2 |
| 14 | DobleVelaAgotamiento: dos velas fuertes corrigen | reversion | EUR, GBP, JPY | 1h | 2 |
| 15 | ADXPullback: retroceso en tendencia fuerte | tendencia | EUR, GBP, JPY, oro | 4h | 2 |
| 16 | IchimokuNube: precio sobre la nube y Tenkan > Kijun | tendencia | EUR, GBP, JPY | 4h | 2 |
| 17 | AsiaReversion en otros pares (replica) | horario | USD/JPY, USD/CHF, GBP/JPY | 1h | 2 |
| 18 | MediodiaEuropa: reversion en la pausa de 10-11 UTC | horario | EUR, GBP | 1h | 2 |
| 19 | CruceMediaLarga: el cierre cruza la SMA60 de 4h | tendencia | EUR, GBP, JPY, oro | 4h | 2 |
| 20 | RangoEstrecho: vela estrecha a favor de la EMA50 | volatilidad | EUR, GBP, JPY | 4h | 2 |

61 pruebas (idea x mercado). Control: comprar siempre en S&P y Nasdaq 1d (referencia de la idea 6).

## Como se mide

- Fuente Dukascopy, 2003-05-01 a 2024-03-31 (indices desde 2011-2013). Una posicion a la
  vez, entrada al cierre de la vela de la senal.
- Payout supuesto 85% (equilibrio 54,05%); con el real de divisas (~78%) el equilibrio es 56,18%.
- **Por idea** (sumando sus mercados): ventaja si z >= 2,81 sobre 54,05% (Bonferroni 0,05/20)
  **y** acierto > 56,18%. Por mercado se informa con z >= 3,15 (Bonferroni 0,05/61).
- Control de la fuente: misma prueba en 2024-04 a 2026-04 con Deriv y con Dukascopy.

## Que decide

- Idea con ventaja -> validacion completa (walk-forward, Monte Carlo, ruido) y forward test en demo.
- El resto -> descartada. No se prueban variantes sobre los mismos datos.

Nota: la prueba no simula que Deriv no deja vencer contratos de divisas entre 21:50 y
23:00 UTC ni el limite de 1h en indices OTC; si una idea pasa, se revisa si es operable.
