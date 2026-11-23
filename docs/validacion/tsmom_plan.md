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

## Enmienda 1 (2026-11-22, antes de bajar un solo dato de B)

Escrita despues de depurar con A y antes de tocar B. Recoge los cambios pedidos o aprobados en la
revision de la sesion TRADING.lider y tres decisiones del usuario del 2026-11-22:
- puede usar otros brokers;
- capital declarado: USD 100;
- seguir en papel.

Lo que esta enmienda no cambia sigue como arriba.

### Script

- `scripts/ml/tsmom.py` del commit `91fc532` (PR #30, en main por el merge `b4b55c4`):
  - blob git `5a60963ca744a93c42f8451c93784d2a45929e32`;
  - SHA-256 (LF) `404a5473aa3207aad1a66acffe868bb589c55ca71f6a03c6f8e1ba7ebb15ba4e`.
- B queda bloqueado en el codigo: `bajar B` y `correr B` exigen `--enmienda1-aprobada`.

### Datos

- Hora de cierre verificada en A (`python -I scripts/ml/tsmom.py hora A`):
  - la vela diaria del datafeed empieza a las 00:00 UTC (366 de 366 velas en 2020 y en 2024);
  - su cierre es igual al cierre 1d de la BD (00:00-24:00 UTC) del mismo dia: 1.797 dias, mediana y
    p95 de la diferencia 0,000000;
  - contra el dia anterior la mediana es 0,002645, y contra el siguiente 0,002120.
- Dukascopy no publica el archivo diario del ano en curso (sale vacio). Para 2026:
  - de enero a octubre se usan los archivos mensuales de 1 hora (`/AAAA/MM/BID_candles_hour_1.bi5`,
    mes 0-11);
  - del 1 al 20 de noviembre se usan los archivos diarios de 1 minuto
    (`/AAAA/MM/DD/BID_candles_min_1.bi5`, solo lunes a viernes), porque el archivo mensual de
    noviembre tampoco existe todavia;
  - las velas de hora o de minuto con volumen > 0 se agrupan en dias UTC 00-24: apertura primera,
    maximo, minimo, cierre ultimo.
- Las series .CMD (Brent, cobre, cacao, algodon) no se ajustan en el cambio de contrato
  (dukascopy.com/swiss/english/cfd/cfd-monthly-adjustment/), asi que el salto se veria como retorno.
  - Salen del escenario principal.
  - Solo se reportan en un secundario marcado SESGADO.
- Indices y ETF de Dukascopy no se ajustan por dividendos (pagina cfd-dividend-adjustment de
  Dukascopy). Es un sesgo pequeno en contra de los largos y a favor de los cortos; no se corrige.
- Los 5 pares con SGD (AUDSGD, EURSGD, NZDSGD, SGDJPY, USDSGD) salen de B.
  - El BIS no publica tasa de politica de Singapur: la MAS fija el tipo de cambio, no una tasa.
  - Sin esa tasa no hay carry.
  - B pasa de 59 a 54 mercados.
- Escenario principal de B: 50 mercados.
  - Composicion: 30 divisas y 20 acciones (10 indices y 10 ETF).
  - Sin las .CMD, la clase materias primas queda vacia en B; el presupuesto se reparte entre 2 clases.
- En A el principal tiene 10 mercados: los metales XAUUSD y XAGUSD no son .CMD.

### Regla de tamano (reemplaza el peso de arriba)

- Peso: w_i = senal_i x min(0,10 / vol_i, 3) x min(1 / (clases activas x mercados activos de su
  clase), 1/12).
  - El tope de 3 evita apalancamientos absurdos con volatilidad muy baja.
  - El tope de 1/12 evita que un mercado solo, en una clase con pocos activos, se lleve media cartera.
  - Lo que sobra no se redistribuye.
- Banda de no operar: el viernes (ultimo dia habil de la semana) un mercado solo se opera en uno de
  estos casos:
  - cambia el signo, o el objetivo es 0;
  - |objetivo - posicion actual| > 25% de |objetivo|.
- Capital de referencia: USD 100.000, para comisiones minimas y tamanos minimos.
- No cambian: la regla 252, el retraso de un dia, la rejilla {63, 126, 252} y la permutacion.

### Costos: el escenario principal pasa a IBKR Pro (pregunta 1, la del veredicto)

Como el usuario acepta otros brokers, el principal deja de ser Deriv CFD con 2% de recargo. Pasa a
IBKR Pro, nivel I, cuenta de USD 100.000, con sus tamanos minimos. Fuentes leidas el 2026-11-22.

| Mercado | Comision | Financiacion | Minimo |
|---|---|---|---|
| Divisas | 0,20 pb, minimo USD 2 por orden | Carry BIS mas el diferencial de IBKR por moneda | Sin minimo; orden < USD 25.000: 2 pb extra (lote impar) |
| CFD de indices | 0,01% | 1,5% anual sobre el nocional | 1 unidad de indice |
| ETF | 0,01%, minimo USD 1, maximo 1% | Largo 1,5%; corto 2,0% (supuesto) | Sin minimo (fracciones) |
| Metales de A | 0,015% | 1,5% (sin tasa de arrendamiento) | No aplica (sin minimo en el codigo) |
| USDTHB | Costos de Deriv | Tabla de swaps de Deriv | Sin minimo en IBKR (usa costos de Deriv) |

Fuentes:
- Spread: medio spread objetivo de Deriv (tabla de arriba, `deriv_especificaciones_2026-11-22.json`)
  como aproximacion. Es mas ancho que el de IBKR en divisas, asi que es conservador.
- Comision de divisas: interactivebrokers.com/en/pricing/commissions-spot-currencies.php.
- Lote impar en divisas: FAQ "odd lot fx transactions" de IBKR.
- Financiacion de divisas: interactivebrokers.com/en/accounts/fees/pricing-interest-rates.php y
  /en/trading/margin-rates.php.
- CFD de indices: interactivebrokers.ie/en/pricing/commissions-cfd-index.php.
- ETF: interactivebrokers.com/en/pricing/commissions-stocks.php.
- Metales: interactivebrokers.com/en/pricing/commissions-cfd.php.

Detalles:
- Diferencial de financiacion de divisas en % sobre la referencia. Cada par es (lo que se pierde al
  tener la moneda, lo que se paga al deberla):
  - USD, EUR, GBP, CAD, AUD y SEK: (0,5, 1,5);
  - JPY y CHF: (0,25, 1,5);
  - NZD: (2,5, 1,5);
  - NOK: (2,0, 1,5);
  - PLN: (2,0, 3,0);
  - MXN: (4,0, 3,0);
  - ZAR: (1,0, 1,5);
  - CNH: IBKR paga 0,5% fijo al tenerla (se pierde la tasa menos 0,5) y cobra 4,0% al deberla.
- ETF:
  - la comision es USD 0,005 por accion, que equivale a 0,005-0,01% con acciones de USD 50-100; se
    toma 0,01%;
  - en corto no hay cifra publicada del prestamo; se supone 2,0% fijo.
- Metales de A: comision de CFD de petroleo como aproximacion. Cacao, cobre y algodon de B usarian la
  misma, pero no estan en el principal.
- USDTHB: IBKR no lista el baht, asi que tambien en el principal usa la tabla de swaps de Deriv en
  lugar del carry BIS.
- Costos x2: spread, comisiones y financiacion de IBKR por 2. El carry BIS no se duplica, porque es
  un rendimiento, no un costo.

Carry de tasas en todos los escenarios salvo el de la tabla de swaps de Deriv:
- se aplica w x (tasa de la moneda base - tasa de la moneda cotizada) x dias calendario / 365;
- tasas: politica diaria del BIS (WS_CBPOL, data.bis.org/static/bulk/WS_CBPOL_csv_flat.zip), con el
  ultimo dato conocido a cada fecha;
- area euro: XM. CNH usa la tasa de China (CN), un limite, porque el CNH offshore tiene tasa propia.

### Pregunta 2: la cuenta del usuario (USD 100)

La misma cartera se corre con USD 100 en IBKR y en Deriv. Cada posicion nueva se redondea hacia abajo
a multiplos del tamano minimo; si no alcanza el minimo, queda en 0.

Tamanos minimos en Deriv MT5 ("Tamano minimo" x "Tamano del contrato", especificaciones del
2026-11-22):
- divisas con version micro: 100 unidades de la moneda base; sin micro: 1.000;
- indices: 0,1 unidad (Japan 225: 10; Spain 35: 1);
- XAUUSD: 0,1 oz; XAGUSD: 0,5 oz;
- materias primas: 1 contrato;
- ETF: 1 accion.

En IBKR: CFD de indices, 1 unidad de indice. Divisas sin minimo, pero con la comision minima de USD 2
y el costo de lote impar. ETF con fracciones.

Otras reglas:
- Deriv se cobra como el secundario Deriv CFD: medio spread mas 2% anual sobre el nocional bruto.
- Escala de ETF: Dukascopy no documenta la escala de precio de sus ETF. Si el ultimo cierre no esta
  entre USD 5 y USD 2.000, se multiplica por potencias de 10 hasta quedar dentro. Se reporta el factor
  aplicado.
- Conversion de una unidad minima a USD:
  - con EURUSD, GBPUSD, USDJPY y USDCHF (de A) y AUDUSD, NZDUSD y USDCAD (de B);
  - HKD fijo a 7,8 por USD;
  - USX (centavo de dolar, cotizacion del algodon): 0,01 USD.
- Un escenario de USD 100 solo cuenta si deja operables al menos 12 mercados en B (5 en A); si no,
  se marca "NO OPERABLE".
  - Operable: el mercado tuvo alguna posicion distinta de 0 en algun momento del periodo.
  - Es un criterio laxo, a favor de la cuenta chica.

Veredicto para el usuario:
- "CANDIDATA PARA SU CUENTA (USD 100)" si la pregunta 1 queda candidata o fuerte y al menos un
  escenario de USD 100 operable tiene t >= 2.
- "LA PRIMA EXISTE PERO NO ES CAPTURABLE CON USD 100" si la pregunta 1 pasa y la 2 no.
- "SIN EVIDENCIA" si la pregunta 1 no pasa.

### Informativos (no deciden nada)

- IBKR y Deriv con USD 10.000.
- IBKR "a escala": costos porcentuales, sin comisiones ni tamanos minimos, con lote impar en todas las
  ordenes.
- Deriv CFD con recargo de 2% (el principal original).
- Tabla de swaps de Deriv sin carry BIS.
- Bruto (spread mas carry).
- Solo precio.
- Con las .CMD (SESGADO).

### Comprobacion de fuga de futuro en A

Script final, principal IBKR, regla 252, evaluacion desde 2004-07-13
(`python -I scripts/ml/tsmom.py fuga A`):

| Version | t | Sharpe | Anual |
|---|---|---|---|
| Normal (retraso 1) | 0,19 | 0,04 | 0,13% |
| Un dia extra (retraso 2) | 0,30 | 0,06 | 0,21% |
| Trampa (retraso 0) | 1,54 | 0,32 | 1,04% |

- El dia extra no mejora claramente (0,30 contra 0,19, ruido).
- La trampa si mejora claramente.
- Cumple el punto 3 de la disciplina de codigo.

### Resultado de A (informativo, universo ya visto)

`docs/validacion/tsmom_A_2026-11-22.txt`: 10 mercados, del 2004-07-13 al 2026-11-20, sin avisos de
datos.

| Escenario | t |
|---|---|
| Principal IBKR USD 100.000 | 0,19 |
| Costos x2 | -1,49 |
| IBKR a escala | 0,56 |
| IBKR USD 100 (7 de 10 mercados operables) | -21,19 |
| Deriv USD 100 (2 de 10 operables: NO OPERABLE) | 1,82 |
| IBKR USD 10.000 | -0,73 |
| Deriv USD 10.000 | 0,32 |
| Deriv CFD 2% | 0,53 |
| Tabla de swaps de Deriv | 0,75 |
| Bruto | 2,07 |
| Solo precio | 2,07 |

- En A la tendencia aparece en bruto (t 2,07), pero los costos de IBKR a USD 100.000 se llevan casi
  todo.
- La volatilidad de la cartera es solo 3,3% anual: el tope de 1/12 con 10 mercados deja un nocional
  medio de 0,56.
- Con USD 100, IBKR pierde 56% anual por la comision minima de USD 2 por orden.
- El mayor mercado (XAUUSD) aporta el 34,3% del P&L.
- A no decide nada.

### Expectativa (actualizada antes de ver B)

- Pregunta 1: como arriba, lo mas probable es "sin evidencia" en el principal.
- Pregunta 2: casi seguro falla.
  - Con USD 100 casi ninguna posicion alcanza el minimo de Deriv.
  - En IBKR la comision minima de USD 2 por orden es 2% del capital por operacion.
- El experimento vale para saber si la prima existe en mercados nunca vistos (escenarios bruto y "a
  escala"), como guia para cuando haya mas capital.

### Limites agregados

- Aceptacion de residentes de Colombia en IBKR: sin confirmar.
- Spreads de IBKR aproximados con los de Deriv.
- Prestamo de ETF en corto sin cifra publicada (2% supuesto).
- CNH con la tasa de China.
- Dividendos sin ajustar en indices y ETF.
