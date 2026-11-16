# Red neuronal LSTM sobre velas de 1h

Plan **pre-registrado** el 2026-11-16, antes de entrenar. La arquitectura y las reglas no se tocan
despues de ver resultados. Solo se cronometro una epoca con datos al azar (2,3 min por 600.000
secuencias en CPU) para confirmar que cabe en tiempo.

## Por que

El LightGBM de binarias (`modelo_ml_plan.md`) acerto 55,5% en la reserva de Deriv al 10%: paso la
regla, pero queda por debajo del punto de equilibrio real (58,0% con payout 72,4%). El LightGBM ve
47 variables resumidas de la ultima vela. Una red recurrente (LSTM) ve la **secuencia completa** de
las ultimas 48 velas y puede encontrar patrones de forma que las variables resumidas no capturan.

## Que se entrena (fijo)

- Mismos datos, mercados y objetivo que el LightGBM de binarias: 7 mercados (5 divisas, oro,
  plata), velas de 1h, subida o bajada del cierre 2 velas despues. Filas que cruzan un hueco de
  datos sin objetivo.
- Entrada por vela (14 valores): retorno de 1h normalizado por volatilidad, rango, cuerpo, mecha
  superior e inferior, hora (seno y coseno) y el retorno de 1h normalizado de los 7 mercados en esa
  hora. Secuencia de 48 velas. Ademas, el mercado (one-hot) entra en la capa final.
- Red: LSTM de 1 capa y 32 unidades -> dropout 0,2 -> capa de 16 (ReLU) -> salida. Adam
  (aprendizaje 0,001), lotes de 2.048, semilla 42. Hasta 8 epocas; se para si la perdida en el
  ultimo 10% (por fecha) del entrenamiento no mejora en 2 epocas, y se queda la mejor epoca.
- Umbrales de cobertura (5/10/20%) con los percentiles de |p - 0,5| en las predicciones de
  entrenamiento, igual que el LightGBM. Una posicion a la vez por mercado.

## Validacion

- **Reserva 1**: Dukascopy 2019-01-01 a 2024-04-01, reentreno anual con todo lo anterior desde
  2003-05-01 (1 dia de separacion). No se repiten los anos de desarrollo 2009-2018 (el LightGBM ya
  mostro que alli el rebote bid-ask de Dukascopy infla el acierto, y cuesta horas de CPU).
- **Reserva 2 (decisiva)**: velas de Deriv desde 2024-04-01 con la red entrenada en todo Dukascopy.

## Que decide

- **Ventaja** (misma regla que el LightGBM): reserva 1 al 10% con acierto > 56,18% y z >= 2,24
  contra 54,05%, y reserva 2 al 10% con acierto > 54,05%.
- **Operable**: reserva 2 al 10% con acierto > 58,0% (punto de equilibrio real de divisas).
- Operable -> se suma al forward test en papel (`forward_ml_plan.md`) como pista nueva, sin tocar
  la pista del LightGBM. Con ventaja pero no operable, o sin ventaja -> se descarta la LSTM.
- Secundaria (informativa): promedio de probabilidades LSTM + LightGBM en la reserva 2.
- Referencia LightGBM en la reserva 2: 10% = 55,47% (7.226 ops), 5% = 59,83% (2.975 ops).

## Como se corre

```
D:\traiding_ml\venv\Scripts\python.exe -I scripts\ml\modelo_lstm.py D:\traiding_ml docs\validacion\eventos_eeuu_2003_2024.json docs\validacion\modelo_lstm_2026-11-16
```

PyTorch (CPU) instalado en `D:\traiding_ml\venv`; modelo en `D:\traiding_ml\modelos\lstm_binarias.pt`.

## Resultado (2026-11-16)

Detalle en `modelo_lstm_2026-11-16.txt` / `.json` (162 min de CPU).

| Prueba | 5% | 10% |
|---|---|---|
| Reserva 1 Dukascopy 2019-2024 | 62,37% (3.261 ops) | 58,55% (10.550 ops, z 9,3) |
| **Reserva 2 Deriv** | 57,33% (2.224 ops) | **56,48%** (5.752 ops) |
| Promedio LSTM + LightGBM, Deriv (secundaria) | 59,94% (2.369 ops) | 57,08% (6.324 ops) |
| Referencia LightGBM, Deriv | 59,83% (2.975 ops) | 55,47% (7.226 ops) |

**Veredicto: VENTAJA pero NO OPERABLE.** La LSTM supera un poco al LightGBM al 10% (56,5% frente a
55,5%), pero queda por debajo del punto de equilibrio real (58,0%). Segun el plan, la LSTM se
descarta y no entra al forward test.

**Comprobacion posterior (no cambia el veredicto):** las senales fuertes de ambos modelos se
concentran en la entrada de 19h a 23h UTC. Deriv no vende Rise/Fall de divisas de menos de 5 horas
entre 21:00 y 23:59 UTC ni con vencimiento entre 21:50 y 23:00 UTC (medido el 2026-11-16). Con el
LightGBM en la reserva de Deriv, divisas al 5%: todas las horas 63,4% (1.725 ops, 56 al mes); solo
horas que se pueden comprar (entrada fuera de 20-23 UTC) **53,1%** (211 ops, 7 al mes); al 10%,
53,9% (1.649 ops). La ventaja del modelo vive en la franja del cierre diario de divisas (rollover,
spreads anchos), justo la que Deriv no permite operar: es muy probable que sea en parte un
artefacto de precios de esa franja, como el rebote bid-ask de Dukascopy.
