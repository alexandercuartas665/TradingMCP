# Plan pre-registrado: E2 en BTC con datos de Bitstamp 2012-2016

Fecha del plan: 2026-11-18 (antes de calcular ningun resultado de este periodo).

## Pregunta

La regla E2 (tendencia con control de volatilidad) se diseno y valido con velas de Binance de
2019 a 2026 (`docs/validacion/cripto_lote2_plan.md`). Bitstamp agrega BTC/USD desde agosto de 2011:
**2012-2016 es un periodo que la regla nunca vio**. Funciona ahi?

## Regla (identica a la del forward test, sin cambiar nada)

- Dia a dia con el cierre UTC: senal = cierre > media de 50 dias (incluido el dia).
- Peso = senal x min(1; 0,5 / volatilidad anualizada de 30 dias) (desv. estandar con ddof=1 x raiz(365)).
- La posicion decidida con el cierre de un dia se aplica al dia siguiente.
- Costo 0,125% por lado sobre el cambio de peso.
- Un solo activo (BTC): no es la cartera de 9 monedas del forward, porque en 2012-2016 solo hay BTC.

## Datos

- `candles` platform = Bitstamp, symbol = cryBTCUSD, timeframe = 1d (velas armadas con las horas con
  operaciones). Calendario diario completo: un dia sin operaciones mantiene el cierre anterior
  (retorno 0).
- Calentamiento desde 2011-08-18; evaluacion del 2012-01-01 al 2016-12-31 (1827 dias).

## Criterio principal (unico, decidido antes)

- Alfa de E2 contra comprar y mantener BTC (regresion de retornos diarios netos, errores Newey-West
  con 5 rezagos, como en el lote 2).
- **E2 FUNCIONA fuera de muestra** si el alfa anual > 0 y su t >= 1,645 (una cola, 5%).
- Si el alfa es > 0 pero t < 1,645: "va en la direccion correcta, sin evidencia suficiente".
- Si el alfa es <= 0: "no funciona en 2012-2016".

## Secundarias (informativas, no cambian el veredicto)

- Retorno anual, Sharpe y caida maxima de E2, H1 (solo senal de 50 dias, peso 1) y comprar y mantener.
- Resultado por ano.
- Costo 0,05% y 0,20% por lado.
- Control de la fuente: en 2017-08 a 2026-11, retornos diarios de E2 calculados con Bitstamp contra
  los calculados con Binance (correlacion y diferencia de retorno anual).

## Lo que esto NO dice

- No valida la cartera de 9 monedas ni el forward en papel; solo si la idea de tendencia con control
  de volatilidad se sostiene en otra epoca del mismo mercado.
- Bitstamp 2012-2013 tiene poco volumen y horas sin operaciones: precios mas ruidosos que hoy.
