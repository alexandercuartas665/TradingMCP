# Plan pre-registrado: momentum de serie temporal diversificado (TSMOM) con tamano por volatilidad

Fecha del plan: 2026-11-22. Escrito ANTES de descargar o mirar cualquier dato del universo B.
Revisado por la sesion TRADING.lider (6 cambios incorporados). Las reglas no se cambian despues de
ver resultados; todo cambio de codigo despues de descargar B es una enmienda escrita con motivo,
subida antes de volver a correr.

## Hipotesis y razon economica

La tendencia de series temporales (Moskowitz, Ooi y Pedersen 2012) es una prima documentada en mas
de 100 anos de futuros: coberturistas e institucionales que rebalancean lento y subreaccion a la
informacion. No es un arbitraje: exige aguantar rachas largas de perdida. La ventaja esta en el
tamano de las ganancias, no en el acierto (por eso fallo en binarias: MomentumSerieTemporal_v1,
49,34%, `lote2_hipotesis_2026-11-15.txt`). Aqui se mide P&L.

## Regla (unica, de la literatura, no ajustada con nuestros datos)

- Senal de cada mercado al cierre del dia t: signo del retorno de 252 dias (+1 largo, -1 corto).
- Volatilidad: desviacion de los retornos diarios de 60 dias, anualizada con raiz de 252.
- Peso: w_i = senal_i x (0,10 / vol_i) x (1 / clases activas) x (1 / mercados activos de su clase).
  Clases: divisas, materias primas, acciones (indices y ETF). La escala no cambia el t (retornos y
  costos son lineales en el peso).
- Rebalanceo: al cierre de cada viernes (o el ultimo dia habil de la semana); los pesos nuevos rigen
  desde el cierre siguiente (un dia de retraso, como E2).
- Rejilla declarada SOLO para la permutacion: retorno de {63, 126, 252} dias. La regla principal es
  252; las otras dos no se reportan como resultado propio.

## Universos

### A: contaminado, solo para desarrollar y depurar el script (informativo)

EURUSD, GBPUSD, USDJPY, USDCHF, GBPJPY, XAUUSD, XAGUSD, US Tech 100, US SP 500, Wall Street 30.
Todos ya usados en experimentos anteriores (ver `REGISTRO_IDEAS.md`).

### B: confirmatorio, nunca visto (lista fija; no se quita ni agrega nada despues)

Armada el 2026-11-22 solo con catalogos, sin mirar precios:
- Deriv CFD MT5: https://deriv.com/es/trading-specifications (pestanas Forex 70, Materias primas
  17, Indices bursatiles 18, ETFs 31; leida 2026-11-22 ~17:00 UTC).
- Dukascopy: metadatos de instrumentos del paquete npm dukascopy-node 1.51.1
  (`instrument-meta-data.json` dentro de `dist/index.js`), campo `startMonthForHourlyCandles`.

Reglas de inclusion (todas):
1. Esta en el catalogo CFD de Deriv (sin las versiones "micro").
2. Esta en el catalogo de Dukascopy con velas desde 2017-12-31 o antes.
3. Nunca usado en ningun experimento nuestro. Se excluyen tambien:
   - pares cuyas dos patas ya se vieron como serie (USD, EUR, GBP, JPY, CHF, XAU, XAG): EURCHF,
     EURGBP, EURJPY, GBPCHF, CHFJPY, XAUEUR, XAGEUR (su retorno es combinacion exacta de series vistas);
   - DXYUSD (indice del dolar: mas del 85% en divisas ya vistas);
   - ETF que replican un indice ya usado o un estilo de el: SPY, IVV, VOO, DIA, IVW; GLD y SLV (oro y plata).
4. No es una divisa fija: se excluyen los pares con HKD (USDHKD, EURHKD, HKDJPY).
5. Spread objetivo de Deriv <= 0,50%: excluye NGAS, CoffeeArab, Sugar, VIXUSD.
6. Mismo instrumento en ambos catalogos: se excluye China H Shares (Dukascopy tiene China A50) y los
   que no estan en Dukascopy (CoffeeRobu, US Mid Cap 400, AGG, ARKK, ERX, HYG, IEMG, LQD, QID, SDS,
   SPXS, TBT, TQQQ, UNG, VOO, VTI, VWO). Fallan la regla 2: GBPNOK, GBPSEK, GBPSGD, XPTUSD, XPDUSD,
   US Small Cap 2000.

Resultado: 59 instrumentos. Fecha de inicio de datos = `startMonthForHourlyCandles` del catalogo.

| Clase | Instrumento Deriv | Codigo Dukascopy | Desde | Spread Deriv |
|---|---|---|---|---|
| Divisas | AUDCAD | AUDCAD | 2006-01-03 | 0,02% |
| Divisas | AUDCHF | AUDCHF | 2006-03-01 | 0,03% |
| Divisas | AUDJPY | AUDJPY | 2003-11-30 | 0,02% |
| Divisas | AUDNZD | AUDNZD | 2006-12-12 | 0,02% |
| Divisas | AUDSGD | AUDSGD | 2007-03-13 | 0,03% |
| Divisas | AUDUSD | AUDUSD | 2003-08-03 | 0,01% |
| Divisas | CADCHF | CADCHF | 2006-01-02 | 0,03% |
| Divisas | CADJPY | CADJPY | 2004-10-25 | 0,02% |
| Divisas | EURAUD | EURAUD | 2005-10-07 | 0,02% |
| Divisas | EURCAD | EURCAD | 2004-10-25 | 0,01% |
| Divisas | EURMXN | EURMXN | 2007-03-13 | 0,05% |
| Divisas | EURNOK | EURNOK | 2004-10-25 | 0,11% |
| Divisas | EURNZD | EURNZD | 2006-01-02 | 0,08% |
| Divisas | EURPLN | EURPLN | 2007-03-13 | 0,04% |
| Divisas | EURSEK | EURSEK | 2004-10-27 | 0,04% |
| Divisas | EURSGD | EURSGD | 2007-03-13 | 0,03% |
| Divisas | EURZAR | EURZAR | 2007-03-13 | 0,10% |
| Divisas | GBPAUD | GBPAUD | 2006-03-22 | 0,02% |
| Divisas | GBPCAD | GBPCAD | 2006-01-02 | 0,02% |
| Divisas | GBPNZD | GBPNZD | 2006-01-02 | 0,03% |
| Divisas | NZDCAD | NZDCAD | 2006-01-02 | 0,05% |
| Divisas | NZDCHF | NZDCHF | 2006-01-02 | 0,04% |
| Divisas | NZDJPY | NZDJPY | 2006-01-02 | 0,03% |
| Divisas | NZDSGD | NZDSGD | 2007-03-13 | 0,04% |
| Divisas | NZDUSD | NZDUSD | 2003-08-03 | 0,02% |
| Divisas | SGDJPY | SGDJPY | 2007-03-13 | 0,03% |
| Divisas | USDCAD | USDCAD | 2003-08-03 | 0,01% |
| Divisas | USDCNH | USDCNH | 2012-06-26 | 0,02% |
| Divisas | USDMXN | USDMXN | 2007-03-13 | 0,04% |
| Divisas | USDNOK | USDNOK | 2003-08-03 | 0,16% |
| Divisas | USDPLN | USDPLN | 2007-03-13 | 0,09% |
| Divisas | USDSEK | USDSEK | 2003-08-03 | 0,06% |
| Divisas | USDSGD | USDSGD | 2004-11-16 | 0,02% |
| Divisas | USDTHB | USDTHB | 2017-02-13 | 0,05% |
| Divisas | USDZAR | USDZAR | 2003-08-03 | 0,06% |
| Materias primas | UK Brent Oil | BRENT.CMD/USD | 2010-12-02 | 0,03% |
| Materias primas | XCUUSD | COPPER.CMD/USD | 2012-03-02 | 0,18% |
| Materias primas | Cocoa | COCOA.CMD/USD | 2017-10-20 | 0,32% |
| Materias primas | Cotton | COTTON.CMD/USX | 2017-10-20 | 0,34% |
| Acciones | Australia 200 | AUS.IDX/AUD | 2014-06-01 | 0,03% |
| Acciones | Europe 50 | EUS.IDX/EUR | 2014-06-01 | 0,03% |
| Acciones | France 40 | FRA.IDX/EUR | 2011-09-18 | 0,05% |
| Acciones | Germany 40 | DEU.IDX/EUR | 2013-09-30 | 0,01% |
| Acciones | Hong Kong 50 | HKG.IDX/HKD | 2013-06-03 | 0,05% |
| Acciones | Japan 225 | JPN.IDX/JPY | 2011-09-18 | 0,02% |
| Acciones | Netherlands 25 | NLD.IDX/EUR | 2017-12-01 | 0,05% |
| Acciones | Spain 35 | ESP.IDX/EUR | 2014-06-01 | 0,04% |
| Acciones | Swiss 20 | CHE.IDX/CHF | 2011-09-18 | 0,06% |
| Acciones | UK 100 | GBR.IDX/GBP | 2011-09-18 | 0,02% |
| Acciones | EEM.US | EEM.US/USD | 2017-01-26 | 0,28% |
| Acciones | EFA.US | EFA.US/USD | 2017-01-26 | 0,17% |
| Acciones | GDX.US | GDX.US/USD | 2017-01-26 | 0,23% |
| Acciones | IJR.US | IJR.US/USD | 2017-05-11 | 0,13% |
| Acciones | IWM.US | IWM.US/USD | 2017-01-26 | 0,07% |
| Acciones | VEA.US | VEA.US/USD | 2017-11-15 | 0,26% |
| Acciones | VNQ.US | VNQ.US/USD | 2017-11-15 | 0,19% |
| Acciones | XLE.US | XLE.US/USD | 2017-11-15 | 0,15% |
| Acciones | XLF.US | XLF.US/USD | 2017-01-26 | 0,31% |
| Acciones | XLK.US | XLK.US/USD | 2017-11-15 | 0,07% |

Avisos (no cambian la lista): Dukascopy llama a Japan 225 "Japan 200+ Index" (se asume el mismo
Nikkei 225); Brent se parece mucho al petroleo WTI (frxUSOIL, ya usado) y GDX al oro; los pares
AUDJPY, CADJPY, NZDJPY, SGDJPY, EUR/GBP-cruzados y USD/xxx tienen una pata ya vista. Divisas son 35
de 59: por eso el presupuesto de riesgo se reparte por clase.

## Datos

- Fuente: archivos diarios del datafeed de Dukascopy (`BID_candles_day_1.bi5`, uno por ano), lado
  BID, para A y B por igual (A se vuelve a bajar asi para que ambos usen el mismo camino de codigo).
- Hora de cierre: la que trae la vela diaria del datafeed. Se verificara SOLO con A (EURUSD contra
  la vela 1d de la BD, que es 00:00-24:00 UTC) y se anotara en la enmienda del hash, antes de bajar B.
- Dias: solo lunes a viernes; si el archivo trae velas de sabado o domingo se descartan. Retorno de
  cada mercado = cierre contra el cierre valido anterior de ese mismo mercado.
- Dias faltantes o festivos distintos entre mercados: ese dia el mercado aporta retorno 0 y conserva
  su posicion; su senal y su volatilidad se calculan solo con sus propios cierres validos.
- Entrada escalonada: cada mercado entra cuando tiene 252 + 60 cierres validos. La evaluacion
  empieza el primer dia con al menos 12 mercados de B con senal y termina el 2026-11-20.
- Velas con precio <= 0 o retornos diarios > 50% en valor absoluto se cuentan y se reportan; si hay
  alguna, el mercado afectado se informa aparte (no se corrige a mano).

## Costos (tres escenarios en la misma corrida; el veredicto sale solo del principal)

- **Principal (Deriv CFD):** retornos de precio, menos medio spread objetivo de Deriv (tabla de
  arriba) por cada unidad de cambio de peso, menos un recargo fijo de 2,0% anual sobre el nocional
  bruto mantenido (suma de |w|), cobrado por dia calendario entre cierres. Igual para todos los
  mercados (es el peor recargo medio medido en la tabla de swaps de Deriv del 2026-11-22).
- **Secundario A:** la tabla asimetrica de swaps de Deriv del 2026-11-22 por lado y por mercado
  (puntos convertidos a % anual del nocional con el precio del dia). Mercados sin fila o con unidad
  ambigua: recargo de 2,0%. Informativo.
- **Secundario B (bruto):** solo medio spread, sin recargo. Informativo: dice si la prima existe en
  estos datos con independencia del broker.

## Estadistico

t de la media del retorno diario neto de la cartera (Newey-West, 5 rezagos), y Sharpe anualizado.

## Niveles de resultado (regla principal 252, escenario principal, universo B)

- **t < 2,0: "sin evidencia".** Se cierra la idea, sin rescates ni variantes.
- **2,0 <= t < 3,0 y permutacion p < 0,05: "candidata".** Solo da derecho a forward en papel.
- **t >= 3,0 y permutacion p < 0,01: "evidencia fuerte".** Igual va primero a forward en papel.
- Para "candidata" y "fuerte" se exige ademas:
  - costos x2 (spread x2 y recargo de 4,0%): Sharpe neto > 0;
  - ningun mercado aporta mas del 50% del P&L total.
  Si falla alguno, el resultado baja a "sin evidencia".
- Si t >= 2,0 pero la permutacion no cumple su nivel, el resultado es "sin evidencia".
- Ningun nivel autoriza dinero real: eso lo decide el usuario.

N = 380 castiga la busqueda (por eso la permutacion incluye la rejilla de 3). Esta prueba es
confirmatoria y unica, con regla fija de la literatura y datos nunca vistos: no es la prueba 381 de
la misma busqueda.

## Permutacion

1.000 permutaciones de los retornos diarios de B (semilla 1). Por cada mercado, los primeros
252 + 60 cierres quedan reales (calentamiento) y el resto se baraja con una clave aleatoria comun
por fecha (conserva la correlacion entre mercados); cada mercado ordena sus dias validos con esa
clave. En cada permutacion se corre la cartera completa con las 3 duraciones y se guarda el maximo t
neto (escenario principal). p = (1 + permutaciones con t maximo >= t de 252 real) / 1.001.

## Disciplina de codigo

1. El script se desarrolla y depura SOLO con A.
2. Antes de bajar un solo dato de B: script subido a main y su hash anotado en este plan (enmienda 1),
   junto con la hora de cierre verificada en A.
3. Comprobacion de fuga de futuro, reportada en A (escenario principal, regla 252):
   - retrasar la senal un dia extra no debe mejorar claramente el t;
   - adelantarla un dia (trampa: usar el cierre de t para operar desde t) debe mejorarlo claramente.
   Si no se cumple, se corrige el codigo antes de la enmienda 1.
4. Despues de bajar B, cualquier cambio de codigo es una enmienda escrita con motivo, subida antes de
   volver a correr.

## Expectativa (escrita antes de ver nada)

Con el recargo de 2% anual sobre el nocional bruto, la cuenta previa dice que lo mas probable es
"sin evidencia" en el escenario principal: una prima de Sharpe 0,3-0,7 sobre 2-3 veces de nocional
bruto deja poco despues de 3-5% anual de recargo. Esta bien. El experimento vale por el escenario
bruto: si la prima aparece ahi y no en el principal, la familia sigue viva solo para otro broker
(decision del usuario); si tampoco aparece en bruto, se cierra la familia de tendencia en estos
mercados.

## Limites

- Los swaps historicos cambiaron con las tasas; el recargo fijo de 2% es una aproximacion.
- Dukascopy no es Deriv: lo que salga bien se confirma en forward.
- Muchos pares de B comparten una pata con series ya vistas; la reserva es por instrumento, no por
  informacion totalmente nueva.
- Multiplicadores fuera de esta prueba: el stop out a 1/M (0,97% con x100,
  `payouts_reales.json`) la convierte en otra estrategia. Multiplicador minimo de forex sin medir.
- Las ETF solo cotizan en horario de EE. UU.; sus velas diarias cierran a otra hora que divisas.

## Script y resultado

`scripts/ml/tsmom.py` (entorno de D:). Resultado en `docs/validacion/tsmom_<fecha>.txt` / `.json`.
