# Forward test del modelo LightGBM de binarias

Plan **pre-registrado** el 2026-11-16, antes de la primera senal contada. No se cambian el modelo,
los umbrales ni las reglas mientras dure la prueba.

## Por que

El modelo LightGBM de binarias (`modelo_ml_plan.md`) es la primera senal que paso la reserva con
datos de Deriv: 55,5% de acierto al 10% de cobertura (7.226 operaciones). El punto de equilibrio
real de divisas es 58,0% (payout 72,4%). Mirando los resultados despues (post-hoc) aparecieron dos
subconjuntos mejores: cobertura 5% (59%) y solo divisas (~62%). Lo post-hoc no cuenta como
prueba: hay que medirlo hacia adelante, con datos que todavia no existen.

## Que se mide (fijo)

- Modelo: `D:\traiding_ml\modelos\lightgbm_binarias.txt` (entrenado con todo Dukascopy hasta
  2024-04-01) y sus umbrales de entrenamiento (`modelo_ml_2026-11-16.json`, `umbrales_final`):
  5% = |p - 0,5| >= 0,1174, 10% = 0,0787.
- Cada hora al cierre de la vela de 1h (HH:00:20 UTC; si la app no responde se reintenta hasta HH:10, despues la vela se salta) se piden las ultimas 400 velas de los 7
  mercados a Deriv por la conexion de la app, se calculan las mismas 47 variables y se guardan
  todas las predicciones. Las marcas de noticias usan el mismo calendario (2003-2024), igual que
  en la reserva con Deriv.
- Senal: nivel 5% o 10%, una posicion a la vez por mercado. Direccion CALL si p > 0,5, PUT si no.
  Duracion 2 horas (Rise/Fall).
- Por cada senal se cotiza Rise/Fall 2h sin comprar (`/api/heavy/payouts`): si Deriv no la ofrece
  (p. ej. vencimiento dentro del cierre 21:50-23:00 UTC de divisas) la senal queda como **no
  disponible**. Entrada = ultimo tick al cotizar; salida = ultimo tick en o antes de entrada + 2h.
  Gana si el precio se movio a favor; empate = pierde (como en Deriv).
- Modo papel por defecto. El usuario puede activar compras de 1 USD en la cuenta DEMO para las
  senales primarias (`--comprar-demo`); la app las rechaza si la cuenta no es demo y pasan por el
  gestor de riesgo.

## Hipotesis primaria

**Divisas (EUR/USD, GBP/JPY, GBP/USD, USD/CHF, USD/JPY), nivel 5%, solo senales disponibles.**

- Decision a las **200 operaciones primarias resueltas**: ventaja si el acierto supera el punto de
  equilibrio del payout medio cotizado con **z >= 1,645** (unilateral 5%). Con payout 72,4% eso
  exige ~63,8% de acierto.
- Corte por futilidad a las 100 operaciones: si el acierto es menor de 55%, se detiene.
- Ventaja -> se propone operar en real con monto minimo y el gestor de riesgo; sin ventaja -> el
  modelo se descarta para binarias.

## Secundarias (informativas, no deciden)

Divisas al 10%, metales al 10%, todas las senales incluidas las no disponibles (mide si el modelo
sigue acertando aunque no se pueda operar) y acierto por hora UTC.

## Ritmo esperado

En la reserva de Deriv hubo ~56 senales primarias al mes, pero la mayoria a las 20h y 21h UTC, que
vencen dentro del cierre de 21:50-23:00 y no se pueden comprar. Si tampoco se puede a las 22h,
quedan unas 20-25 al mes: llegar a 200 puede tomar 8-10 meses. Se informa el avance en cada revision.

## Como se corre

```
D:\traiding_ml\venv\Scripts\python.exe -I scripts\ml\forward_lightgbm.py            # bucle (papel)
D:\traiding_ml\venv\Scripts\python.exe -I scripts\ml\forward_lightgbm.py --resumen  # informe
```

Registros en `D:\traiding_ml\forward\` (predicciones.jsonl, senales.jsonl, resultados.jsonl,
forward.log). Arranca solo al iniciar sesion en Windows (acceso directo en la carpeta Inicio,
`scripts\ml\forward_inicio.ps1`).
