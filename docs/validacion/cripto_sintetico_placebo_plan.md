# Cripto: entrenar con datos sinteticos y prueba placebo

Plan **pre-registrado** el 2026-11-18, antes de generar ningun dato sintetico o placebo. Los
parametros quedan fijos.

## Por que

- **Parte A (sinteticos)**: el modelo LightGBM de cripto (H4 en `cripto_plan.md`) brillo en la
  reserva 1 (57,4%, z 4,11) y fallo en la reserva ciega (52,9%, -0,11% neto). La pregunta es si
  agregar datos sinteticos al entrenamiento evita que memorice. La expectativa previa, escrita antes
  de medir, es que **no**: los sinteticos se fabrican con los mismos datos reales y no traen
  informacion nueva.
- **Parte B (placebo)**: se fabrican mercados donde por construccion no hay forma de predecir la
  direccion, y se les pasa exactamente el mismo proceso. Mide cuantas veces nuestro metodo
  "encuentra" ventaja donde no la hay, y cuanto de la menor caida de E2 viene solo de su control de
  volatilidad.

## Parte A: LightGBM con datos reales mas sinteticos

**Generador (bootstrap por semanas, solo con datos de entrenamiento):**
- Se toman semanas completas reales (lunes 00:00 a lunes 00:00 UTC) de la ventana de
  entrenamiento, las 9 monedas juntas (conserva la correlacion entre monedas, la hora y el dia).
- Se eligen al azar con reemplazo y se encadenan:
  - el precio sintetico se arma con los retornos hora a hora;
  - apertura, maximo y minimo son relativos al cierre anterior;
  - volumen, compra agresiva y numero de operaciones se normalizan por su media real de 30 dias
    anteriores, para que semanas de anos distintos sean comparables.
- Una semana solo se usa si termina antes del corte de entrenamiento menos 1 dia. El generador nunca
  ve datos del periodo que se evalua.
- Variables, etiqueta (sube o baja en 24h) y decisiones cada 4h: identicas a H4.

**Variantes** (mismos hiperparametros de H4):
- **S0**: solo reales (debe reproducir H4).
- **S1**: reales + sinteticos del mismo tamano (1x).
- **S2**: reales + sinteticos 3 veces el tamano (3x).

Para S1 y S2 hay 5 semillas del generador. Los umbrales del 5/10/20% se calculan solo con las
predicciones sobre los datos reales de entrenamiento, para que la cobertura sea comparable.

**Evaluacion:** igual que H4.
- Reserva 1: reentreno anual, se mide 2021-01-01 a 2025-06-01.
- Reserva 2 (ciega): 2025-06-01 en adelante, entrenando con todo hasta 2025-06-01.
- Nivel 10%, costo 0,25% ida y vuelta.

**Que decide** (Bonferroni para 2 variantes: z >= 1,96 unilateral):
- Una variante tiene ventaja si en **al menos 4 de 5 semillas** se cumple las dos cosas:
  - en la reserva 1 el neto medio por operacion es positivo con z >= 1,96;
  - en la reserva 2 el neto medio es positivo.
- **Aporta** si ademas el neto medio en la reserva 2 (promedio de semillas) supera al de S0.

## Parte B: placebo

**Generador de placebo (cambio de signo):** se multiplica por +1 o -1 al azar el retorno de cada
periodo, el mismo signo para las 9 monedas en ese periodo.
- **Se conserva:** el tamano de los movimientos, las rachas de volatilidad, el volumen y la
  correlacion entre monedas.
- **Se destruye:** cualquier relacion entre el pasado y la direccion futura.
- En velas de 1h la vela entera se refleja alrededor del cierre anterior (maximo <-> minimo) y la
  compra agresiva pasa a ser la venta agresiva (volumen - compra agresiva).

**B1, modelo (H4 completo, S0):** 30 mercados placebo con cambio de signo hora a hora, desde 2017.
Se informa:
- la distribucion del acierto y del z de la reserva 1 y la reserva 2 al 10%;
- cuantos placebos llegan a z >= 2,24 (el criterio de H4);
- en que percentil queda el z real de 4,11.

**B2, reglas diarias:** 1.000 mercados placebo con cambio de signo dia a dia, desde 2019.
- Se pasan H1 y H2 (`cripto_plan.md`) y las seis estrategias del lote 2 con su criterio
  pre-registrado (diseno t >= 2,39 y alfa positivo en la reserva).
- Se informa:
  - el porcentaje de placebos con al menos una estrategia del lote 2 "confirmada";
  - el percentil del t real de E2 (2,49) y de H1 (2,64) en diseno;
  - la caida maxima mediana de E2, H1 y comprar y mantener en los placebos (cuanto de la proteccion
    de E2 sale solo de su control de volatilidad).

En B1 se informa tambien el **z bruto** (antes de costos). En un placebo la ganancia bruta esperada
es 0, asi que el z bruto deberia comportarse como una normal estandar: desviacion cercana a 1 y
alrededor de 5% de los placebos con z bruto >= 1,645.

**Lectura (fija):**
- El z del modelo esta inflado si ocurre cualquiera de estas dos cosas:
  - mas del 5% de los placebos pasa el criterio de H4;
  - la desviacion del z bruto entre placebos es mayor de 1,5.
- Una causa posible es que las operaciones de distintas monedas a la misma hora estan
  correlacionadas. Si esta inflado, los z reportados del modelo se tratan como optimistas, y el z
  real se compara con la distribucion de los placebos.
- Si el t real de E2 o H1 no supera el percentil 95 de sus placebos, su resultado en diseno no se
  distingue del azar.

## Script

`scripts/ml/cripto_sintetico.py` (entorno de D:). Resultado en
`docs/validacion/cripto_sintetico_<fecha>.txt` / `.json`.
