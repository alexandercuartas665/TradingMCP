# Plan pre-registrado: modelo-filtro del brief (oro, Nasdaq, USDJPY en M15)

Fecha del plan: 2026-11-18, antes de bajar los datos de minuto y de calcular cualquier resultado.
Base: "Brief de entrenamiento de modelos 2003-2026" (documento de trabajo del companero, 18-11-2026).
Las reglas de este plan no se cambian despues de ver resultados. Si falla un criterio:
**NO HAY EVIDENCIA DE VENTAJA**, sin ajustar para rescatar (seccion 8 del brief).

## Pregunta

Un modelo que estima la probabilidad de que una senal de una estrategia base termine en ganancia
neta (filtro de entradas, no prediccion de direccion) da ventaja que pase la validacion del brief?

## Datos

- Dukascopy, velas de 1 minuto **bid y ask** (spread real), UTC, sin velas sin volumen.
  Oro (XAUUSD) y USDJPY desde 2003-05; Nasdaq 100 (USATECHIDXUSD = OTC_NDX) desde 2011-09.
  Hasta 2026-10-31. Se agregan a M15; H1 y D1 se arman con las mismas velas (00 UTC).
- Guardado en `D:\traiding_ml\brief\` (fuera del repo). Scripts: `scripts/ml/brief_datos.py`
  (descarga) y `scripts/ml/brief_experimento.py` (experimento).
- No hay fichas tecnicas de las estrategias EST-0xx: las estrategias base se definen aqui con la
  logica general del brief y parametros fijos.

## Estrategias base (senal al cierre de la vela M15 t, entrada en la apertura de t+1)

Indicadores: EMA, RSI 14, ADX 14 con +DI/-DI, ATR 14, Bollinger 20/2, SAR parabolico (0,02/0,2),
canal = maximo/minimo de las 10 velas anteriores. "H1" y "D1" = ultima vela H1/D1 cerrada.

| Activo | Familia | Compra (venta simetrica) | Filtro fijo |
|---|---|---|---|
| Oro | A. Ruptura del dia anterior | cierre cruza por encima del maximo del dia anterior (primera vez en el dia) | cierre > EMA200 H1 y SAR H1 bajo el precio; senal entre 07:00 y 09:59 UTC |
| Oro | D. Pullback | cierre > EMA200 H1; minimo <= EMA21 M15, RSI 14 < 40 y cierre > EMA21 | 07:00-09:59 UTC |
| Nasdaq | A. Canal de 10 velas | cierre > maximo de las 10 velas anteriores | 13:00-20:59 UTC |
| Nasdaq | B. Cruce EMA + ADX | EMA12 cruza por encima de EMA26 con ADX > 30 | cierre > EMA200 H1; 13:00-20:59 UTC |
| USDJPY | A. Ruptura con SAR | cierre > maximo de 10 velas y SAR M15 bajo el precio | cierre > EMA200 H1 |
| USDJPY | C. Reversion Bollinger | cierre vuelve dentro de la banda inferior (cierre previo < banda, cierre >= banda) | ADX 14 < 25 |

Una posicion a la vez por activo y familia (las senales con una posicion abierta se ignoran).

## Salida y etiqueta (triple barrera, fija)

- Stop = 1,5 x ATR14 M15 de la vela de senal. Objetivo = 1,5 R (2,25 x ATR). Salida por tiempo al
  cierre de la vela 16 (4 horas). Sin trailing.
- Compra: entra al **ask** de apertura de t+1; stop y objetivo se miran con el **bid** de cada vela;
  si en la misma vela se tocan los dos, cuenta el stop; si una vela abre pasando el stop, sale a esa
  apertura. Venta simetrica (entra al bid, se mira el ask).
- Deslizamiento: 0,5 x spread de entrada en la entrada y otro tanto en la salida.
- Resultado en R neto = (salida - entrada) / (1,5 x ATR), con todos los costos. Etiqueta = R neto > 0.

## Variables del modelo (20, al cierre de t)

Distancia del cierre a EMA200 H1 y a EMA50 D1 (en ATR, por la direccion de la senal); lado del SAR H1
(+1 a favor, -1 en contra); RSI 14; WPR 14; ADX 14; (+DI - -DI) por la direccion; percentil del ATR14
en 100 dias; ancho de Bollinger / ATR; distancia al maximo y al minimo del dia anterior (en ATR);
posicion en el canal de 10 velas; vela interior (0/1); hora; dia de la semana; minutos desde el
inicio de la sesion (Asia 00, Londres 07, Nueva York 13, cierre 21 UTC); percentil del ADX H1 en 100
dias; percentil de la volatilidad realizada de 1 dia en 100 dias; spread / ATR; direccion (+1/-1);
familia (0/1).

## Modelo

- LightGBM por activo con parametros fijos: 200 arboles, tasa 0,03, 15 hojas, minimo 50 por hoja,
  80% de filas y columnas, semilla 1. Clasificador de R neto > 0.
- **Caminando por anos**: para el ano A se entrena con todas las senales cerradas antes del
  1-ene de A menos 1 dia (purga). Primer ano predicho: el que tenga al menos 3 anos de entrenamiento
  (oro y USDJPY 2006, Nasdaq 2015).
- Se toma la senal si p >= la mediana de p sobre el propio conjunto de entrenamiento (guarda ~la
  mitad de las senales; el umbral no se ajusta).
- Sistema = estrategias base + filtro. Resultados en R por operacion, con todas las operaciones
  predichas caminando (2006/2015 a 2026-10).

## Criterios (seccion 8 del brief; se evaluan por activo sobre el sistema con filtro)

1. Expectativa: R medio neto > 0 en todo el periodo caminado.
2. Caminata anual: >= 5 de los 6 ultimos anos completos (2020-2025) con suma de R > 0.
3. Anos de estres (2008, 2020, 2022, los que existan): la suma de R de esos anos >= 0.
4. Entradas aleatorias: la suma de R supera el **percentil 95** de 1000 simulaciones con entradas
   al azar (mismo numero de operaciones, mismo ano, misma hora y misma direccion, misma salida y
   costos). Con 3 activos se exige ademas el percentil 98,3 (Bonferroni) para declarar ventaja.
5. Costos al doble (spread y deslizamiento x2): suma de R > 0.
6. IS/OOS 70/30 por fecha: modelo entrenado en el 70% inicial (purga 1 dia); en IS el sistema se
   mide con predicciones fuera de pliego (5 bloques por fecha, purga 1 dia). OOS: R medio > 0 y
   >= 50% del R medio IS (si el IS es <= 0, falla).
7. Frecuencia: >= 100 operaciones del sistema en el periodo caminado.

Veredicto por activo: **EVIDENCIA DE VENTAJA** solo si pasa los 7 (y el percentil 98,3); si no,
**NO HAY EVIDENCIA DE VENTAJA**.

## Informativo (no cambia el veredicto)

- Las estrategias base sin filtro, con los mismos criterios.
- Compras y ventas por separado (seccion 6 del brief); por familia; por ano.
- Sensibilidad: stop 2,0 ATR y objetivo 2 R.

## Limites

- Costos de Dukascopy (CFD); en Deriv estos activos se operan con multiplicadores, con otra
  estructura de costo: lo que salga bien se confirma ahi en forward.
- Precios bid/ask de Dukascopy en 2003-2008 tienen spreads amplios; se usan tal cual (costo real
  de esa epoca).

## Enmienda 1 (2026-11-18, antes de calcular cualquier resultado): primero en H1

Al bajar los datos, Dukascopy empezo a responder 429 (demasiadas peticiones): ~50 s por archivo
de minuto; los ~39.000 archivos del plan tomarian dias. Decision del usuario: correr ya el mismo
experimento en **H1** y dejar la version M15 (el plan de arriba, sin cambios) para cuando los datos
de minuto esten completos.

Cambios para la version H1 (todo lo demas igual: estrategias, horas de sesion, triple barrera,
costos, 20 variables, modelo, caminata, 7 criterios y percentil 98,3):

- Velas: H1 bid de Dukascopy ya guardadas en la base (`candles`, platform Dukascopy, frxXAUUSD,
  frxUSDJPY y OTC_NDX, 1h). D1 se arma con ellas (dias con al menos 5 horas).
- Las senales se calculan en H1: "M15" del plan pasa a H1 (EMA21, EMA12/26, RSI, ADX, ATR,
  Bollinger, SAR y canal de 10 velas en H1). El contexto H1 es la propia vela H1 cerrada.
- Salida por tiempo: 4 velas H1 (las mismas 4 horas que 16 velas M15). Stop 1,5 x ATR14 H1.
- Spread: fijo por activo y ano = mediana de (cierre ask - cierre bid) de las velas H1 de **junio**
  de ese ano (o del primer mes con datos), con el archivo horario ask de Dukascopy contra el bid de
  la base. El ask de cada vela = bid + ese spread. Deslizamiento 0,5 x spread por lado, igual.
- Percentiles "de 100 dias" = 2.400 velas H1; volatilidad realizada de 1 dia = 24 velas H1.
- Una entrada solo si la vela siguiente llega dentro de 2 horas.
