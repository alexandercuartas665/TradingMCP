# Modelo de aprendizaje automatico (en vez de reglas fijas)

Plan **pre-registrado** el 2026-11-16, antes de entrenar ningun modelo. Las reglas no se tocan
despues de ver los resultados de las reservas.

## Pregunta

Las reglas fijas (41 ideas en binarias, 4 en multiplicadores) no muestran ventaja en 1h/4h. Un
modelo que combina muchas variables a la vez encuentra una ventaja que las reglas no ven?

## Datos

- Velas de 1h Dukascopy, 7 mercados: EUR/USD, GBP/JPY, GBP/USD, USD/CHF, USD/JPY, oro, plata.
  2003-05 a 2024-03 (nunca usadas para entrenar nada). Un solo modelo para los 7 mercados
  (el mercado es una variable mas).
- Segunda reserva: velas de 1h de **Deriv** 2024-04-01 a hoy (la fuente con la que se opera).
- Entorno y modelos en `D:\traiding_ml` (fuera del repo). Script: `scripts/ml/modelo_ml.py`.

## Variables (solo informacion disponible al cierre de la vela t)

- Retornos logaritmicos de 1, 2, 3, 4, 6, 12, 24 y 120 velas, divididos por la volatilidad.
- Volatilidad de 24 y 120 velas y su razon; rango, cuerpo y mechas de la ultima vela.
- RSI 14, posicion en Bollinger 20, diferencia EMA 12/26, CCI 20, distancia al maximo y minimo
  de 24 velas.
- Hora (seno/coseno) y dia de la semana.
- Noticias: si hay NFP o FOMC en las proximas 2 horas o en las 2 anteriores
  (`eventos_eeuu_2003_2024.json`; en la reserva Deriv posterior a 2024 se deja en 0).
- Retorno de 1 y 6 velas de los otros 6 mercados en la misma hora.

## Objetivos y modelos

- **Binarias:** sube o baja el cierre 2 velas despues (como `duracion=2` del sistema).
- **Multiplicadores:** sube o baja el cierre 6 velas despues.
- Dos modelos, con hiperparametros fijos (no se ajustan):
  - Regresion logistica (variables estandarizadas, C = 1).
  - LightGBM (300 arboles, tasa 0,03, 31 hojas, minimo 500 por hoja, 80% de filas y columnas).
- Se descartan las filas cuyo horizonte cruza un hueco de datos (fin de semana).

## Validacion

- **Entrenamiento caminando:** para predecir el ano A se entrena con todo lo anterior a A
  (desde 2003-05), dejando 1 dia de separacion. Desarrollo: anos 2009-2018 (se pueden mirar
  para encontrar errores de programa, no para cambiar variables ni parametros).
- **Reserva 1:** 2019-01-01 a 2024-04-01, mismo esquema (reentreno anual).
- **Reserva 2:** modelo entrenado con todo Dukascopy hasta 2024-04-01, aplicado a Deriv
  2024-04-01 en adelante.
- Se opera solo el **10% de predicciones mas seguras**: el umbral es el percentil 90 de
  |p - 0,5| en las predicciones del propio periodo de entrenamiento. Una posicion a la vez por
  mercado. Tambien se informa el 5%, el 20% y el 100% (solo informativo).

## Que decide (4 pruebas: 2 modelos x 2 objetivos; Bonferroni z >= 2,24)

- **Binarias:** ventaja si en la reserva 1 el acierto supera el 54,05% (payout 85%) con
  z >= 2,24, el acierto puntual supera el 56,18% (payout 78%) y en la reserva 2 el acierto
  supera el 54,05%.
- **Multiplicadores:** ventaja si en la reserva 1 el retorno medio a favor (por unidad de
  nocional) menos una comision de 0,02% tiene z >= 2,24 y en la reserva 2 es positivo. Se
  informa tambien con 0,01% y 0,04%.
- Referencia: acierto de comprar siempre (tendencia de fondo), para no confundir deriva con
  prediccion.
- Si ninguna de las 4 pasa: el aprendizaje automatico con velas de 1h tampoco encuentra ventaja;
  no se ajustan modelos sobre las reservas.

## Resultado (2026-11-16)

Detalle en `modelo_ml_2026-11-16.txt` / `.json`. Modelos en `D:\traiding_ml\modelos`.

| Prueba | Reserva 1 (2019-2024, 10%) | Reserva 2 Deriv (10%) | Veredicto |
|---|---|---|---|
| Binarias, logistica | 53,96% (z -0,28) | 52,28% | sin ventaja |
| **Binarias, LightGBM** | **61,86% (z 16,9)** | **55,47%** (7.226 ops) | **VENTAJA segun la regla** |
| Multiplicadores, logistica | -6,8 (z con 0,02%) | retorno negativo | sin ventaja |
| Multiplicadores, LightGBM | 0,08 (z con 0,02%) | +0,0225% | sin ventaja (z < 2,24) |

Comprobaciones posteriores (no pre-registradas, solo para entender el resultado):

- **Rebote del spread en Dukascopy.** "Apostar contra la ultima vela" acierta 51-53% en divisas y
  63% en plata en 2003-2008 (precios bid con spread grande). Por eso el desarrollo y la reserva 1
  salen tan altos (60-70%). La reserva 2 (Deriv, la fuente con que se opera) es la que cuenta.
- **Entrada realista en Deriv** (apertura de la vela siguiente en vez del cierre): 55,0% al 10% y
  59,0% al 5%. La ventaja no depende del ultimo tick.
- **Por mercado en Deriv (5%, entrada realista):** EUR/USD 59%, GBP/JPY 61%, GBP/USD 54%,
  USD/CHF 65%, USD/JPY 67%; plata 55% y oro 53%. Solo divisas: ~62% en 1.725 ops.
- **Por hora:** casi todas las senales de divisas se concentran entre las **19:00 y las 22:00 UTC**
  (tarde de Nueva York y cierre diario), donde el modelo aprendio una reversion. Hay que
  confirmar que Deriv ofrece Rise/Fall de 2 horas en divisas a esas horas y con que payout.

Conclusion: es la primera senal que pasa una reserva con datos de Deriv, pero el acierto del
10% (55,5%) queda por debajo del equilibrio con payout 78% (56,2%). El 5% mas seguro y el
filtro de solo divisas son elecciones posteriores: se tratan como hipotesis para un forward test
en demo, no como ventaja demostrada. Pasos: medir payout y disponibilidad de 19-22 UTC con el
mercado abierto, y forward test (lo lanza el usuario).
