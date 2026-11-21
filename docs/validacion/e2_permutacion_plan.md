# Plan pre-registrado: E2 contra permutaciones (los 4 portones)

Fecha del plan: 2026-11-21, antes de calcular cualquier permutacion o caminata de este experimento.
Las reglas no se cambian despues de ver resultados.

## Por que

Metodo de validacion tomado de un articulo sobre investigacion de estrategias con IA (prueba de
permutacion de barras, al estilo de T. Masters): si al barajar el orden de los precios el mismo
proceso de optimizacion encuentra una "ventaja" igual de buena, la ventaja real nunca existio.

Ya probado antes (`cripto_sintetico_placebo_plan.md`, parte B2): E2 con parametros fijos queda en el
percentil 99,9 de 1.000 placebos de cambio de signo. Lo que **no** se probo:
- el sesgo de **elegir** parametros: el placebo no reoptimizaba en cada mercado falso;
- una **caminata con reoptimizacion** contra datos barajados.

## Regla E2 y su familia de parametros

E2 (`cripto_lote2_plan.md`): senal = cierre > media de N dias; peso por moneda =
(1/n) x min(1; objetivo / volatilidad anualizada de V dias); la posicion del cierre de t se aplica
en t+1; costo 0,125% por lado sobre el cambio de peso. Cartera de igual peso entre las monedas con
datos ese dia (9 monedas de Binance, velas diarias UTC). Estadistico: t del alfa (Newey-West, 5
rezagos) de los retornos diarios netos contra comprar y mantener las mismas monedas, como en el
lote 2. Retorno medido en cada dia (no por operacion).

Rejilla (80 combinaciones): N en {20, 30, 40, 50, 75, 100, 150, 200}; V en {20, 30, 60}; objetivo en
{0,3; 0,5; 0,8} mas "sin control de volatilidad" (= H1, no depende de V). E2 = (50, 30, 0,5).

**Control previo:** con datos reales la rejilla debe reproducir el t de diseno del lote 2
(E2 2,49; H1 2,64). Si no, se corrige el codigo antes de correr ninguna permutacion.

## Permutacion

- Se barajan los **retornos diarios de cierre a cierre** dentro de una ventana; lo anterior queda
  real (calentamiento). Mismo punto de partida y mismo punto final, misma distribucion de retornos;
  se destruye cualquier orden temporal (tendencias y rachas de volatilidad). Como las reglas solo
  usan cierres diarios, barajar los retornos de cierre equivale a la permutacion de barras.
- Las 9 monedas se barajan con la misma clave por dia (conserva la correlacion entre monedas). Una
  moneda que empieza despues del inicio de la ventana ordena sus dias validos con esa misma clave.
- Semilla 1.

## Los 4 portones

1. **Excelencia en diseno** (ya medida, 2019-01-01 a 2025-06-01): E2 t 2,49 >= 2,39 del lote 2. Pasa.
2. **Permutacion en diseno con optimizacion**: 1.000 permutaciones de 2019-01-01 a 2025-06-01. En
   cada una se calcula el t de las 80 combinaciones y se guarda el **maximo**. Con datos reales se
   hace lo mismo.
   - p = (1 + permutaciones con t maximo >= t maximo real) / 1.001. **Pasa si p < 1%.**
   - Informativo: el p del t de E2 (2,49) contra esa misma distribucion de maximos.
3. **Caminata con reoptimizacion** (datos reales): cada 30 dias, desde 2021-08-17 (4 anos despues
   del primer dato de Binance) hasta 2026-11-16, se elige la combinacion con mayor t en los 4 anos
   anteriores y se opera los 30 dias siguientes. Los pesos se cosen y los costos se cobran sobre los
   pesos cosidos. **Pasa si el alfa de la caminata (2021-08-17 a 2026-11-16) es > 0.**
4. **Permutacion de la caminata**: 200 permutaciones de los datos desde 2021-08-17 (los primeros 4
   anos quedan reales); en cada una se repite la caminata completa del porton 3.
   - p = (1 + permutaciones con t >= t real) / 201. **Pasa si p < 1%** (el tramo cubre mas de 2 anos).

4b. **Bitstamp 2012-2016** (BTC solo, E2 fijo como en `e2_bitstamp_2012_2016_plan.md`): 1.000
   permutaciones de los retornos diarios de 2012-01-01 a 2016-12-31 (calentamiento real desde
   2011-08-18). p del t real (3,37). **Pasa si p < 1%.**

**Veredicto:** E2 "pasa los portones" solo si pasa 2, 3, 4 y 4b. Si falla alguno, se dice cual y
que significa; no se ajusta nada para rescatarlo.

## Informativo (no cambia el veredicto)

- Mapa del t de las 80 combinaciones con datos reales: si E2 esta en una meseta (vecinos con t
  parecido) o en un pico aislado.
- Combinaciones elegidas por la caminata y su comparacion con E2 fijo en el mismo tramo.
- Histogramas de las permutaciones con el valor real marcado.

## Que cambia segun el resultado

- El forward en papel de E2 sigue igual en cualquier caso: es la prueba que decide.
- Si falla, los resultados de diseno de la familia de tendencia se leen como posibles hallazgos de
  la optimizacion, y una eventual decision de dinero real exige mas tiempo de forward.

## Limites

- Barajar destruye tambien las rachas de volatilidad: el control de volatilidad de E2 deja de
  servir en los datos barajados. La prueba mide tendencia y volatilidad predecible juntas.
- El sesgo de supervivencia en la eleccion de las 9 monedas no se corrige aqui.
- La busqueda real que hicimos fue mas amplia que esta rejilla (H1-H4, E1-E6): el porton 2 mide el
  sesgo de optimizar parametros dentro de la familia de E2, no el de todo el historial de ideas.

## Script

`scripts/ml/e2_permutacion.py` (entorno de D:). Resultado en
`docs/validacion/e2_permutacion_<fecha>.txt` / `.json`.

## Resultado (2026-11-21)

Script `scripts/ml/e2_permutacion.py`; detalle en `e2_permutacion_2026-11-21.txt` y `.json` (12,9 min).
Control: con datos reales la rejilla reproduce el lote 2 (E2 t 2,491; H1 t 2,634).

| Porton | Real | Permutaciones (p95 / p99) | p | Resultado |
|---|---|---|---|---|
| 2. Diseno con optimizacion (1.000) | t max 2,94 (N20 V30 obj 0,3) | 2,17 / 3,00 | 0,013 | FALLA (por poco) |
| 3. Caminata 2021-08 a 2026-11 | alfa 5,3% anual, t 0,59 | - | - | PASA (alfa > 0) |
| 4. Caminata permutada (200) | t 0,59 | 1,21 / 1,77 | 0,144 | FALLA |
| 4b. Bitstamp 2012-2016 (1.000) | t 3,37 | 1,50 / 2,02 | 0,001 | PASA |

**Veredicto: E2 NO PASA los portones (falla 2 y 4).**

Lectura:
- Porton 2: despues de descontar la busqueda entre 80 combinaciones, el resultado de diseno queda en
  p 1,3% (E2 exacto, 2,9%): pasaria al 5% pero no al 1% exigido. La ventaja de diseno es probable,
  no concluyente.
- Portones 3 y 4: desde agosto de 2021 la caminata gana algo (5,3% anual de alfa, caida 50% frente a
  79% de comprar y mantener), pero no se distingue de datos barajados (p 14%). E2 fijo lo hizo
  mejor en el mismo tramo (alfa 10,5%, t 1,23, caida 35%), sin prueba de permutacion propia.
- Porton 4b: en 2012-2016 la tendencia era clara: el t real supera las 1.000 permutaciones.
- Conjunto: la tendencia en cripto fue real (2012-2016 y diseno 2019-2021), pero en el tramo
  reciente no hay evidencia de que siga ahi. Coincide con el estudio cripto (ventaja casi toda en
  2019-2021, desde 2022 no significativa).
- Meseta (informativo): N de 20 a 50 dias da t 2,3-2,9 con cualquier control de volatilidad; desde
  75 dias cae a 0,5-1,8. E2 (N50) no es un pico aislado. La caminata elige sobre todo N20.

Consecuencia (fijada en el plan): el forward en papel de E2 sigue igual. Cualquier decision de
dinero real exige mas tiempo de forward; los resultados de diseno de la familia de tendencia se leen
como posibles hallazgos de la optimizacion.
