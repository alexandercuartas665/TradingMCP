# Estudio de viabilidad en cripto

Plan **pre-registrado** el 2026-11-17, antes de calcular cualquier resultado. Solo se reviso la
cobertura de los datos (fechas y numero de velas). Las reglas no se tocan despues.

## Por que

En binarias de Deriv la casa cobra ~14% por operacion (payout 72%) y bloquea las horas donde los
modelos encontraban algo. En un exchange con libro de ordenes el costo es una comision de ~0,1% por
lado y la contraparte es otro participante. Cripto ademas tiene datos que forex en Deriv no tenia:
volumen, numero de operaciones y volumen de compradores agresivos. La literatura academica reporta
momentum en cripto (p. ej. Liu y Tsyvinski, datos hasta 2018); los anos posteriores son fuera de
muestra respecto a esa literatura.

## Datos

- **Binance spot, velas de 1h** (archivos publicos data.binance.vision), 9 monedas: BTC, ETH, BNB,
  LTC, XRP, ADA, LINK, DOGE, SOL (contra USDT). Desde agosto 2017 (cada moneda desde que cotiza)
  hasta 2026-11-16. Script: `scripts/ml/cripto_descargar.py`. Datos en `D:\traiding_ml\cripto\datos`.
- **Deriv cryBTCUSD y cryETHUSD, velas de 1h**: Deriv solo entrega 1 ano (2025-11-17 a 2026-11-17).
  Se usan como control de la fuente y para la prueba de multiplicadores de Deriv.
- Dia = dia UTC. Semana = lunes a domingo UTC.

## Costos (fijos)

- Principal: **0,125% por lado** (comision taker 0,10% + deslizamiento 0,025%), 0,25% ida y vuelta.
- Sensibilidad: 0,05% y 0,20% por lado.
- Las posiciones cortas suponen futuros perpetuos con el mismo costo; no se cuenta la tasa de
  financiamiento (funding), que en promedio suele favorecer al corto.
- Deriv multiplicadores (BTC/ETH): comision medida el 2026-11-17, 0,032% (BTC) y 0,085% (ETH) del
  nocional al entrar; multiplicador minimo x100 -> stop out cuando el movimiento en contra se come
  el stake (~1%).

## Hipotesis principales (Bonferroni para 4: z >= 2,24, unilateral)

Las reglas H1-H3 son clasicas y sus parametros vienen de la literatura: no se ajusta nada con
estos datos. Por eso se prueban en todo el periodo **2019-01-01 a 2026-11-16** (fuera de muestra
respecto a esa literatura). Cartera de igual peso entre las monedas que existan ese dia.

- **H1 tendencia diaria (solo compras, spot)**: al cierre de cada dia, comprado al dia siguiente si
  el cierre esta sobre su media de 50 dias; si no, fuera.
- **H2 momentum de serie (largo/corto)**: cada lunes, comprado si el retorno de las 4 semanas
  anteriores (28 dias) es positivo, vendido si es negativo; se mantiene la semana.
- **H3 momentum cruzado (largo/corto)**: cada lunes se ordenan las monedas por retorno de 28 dias;
  comprado en las 3 mejores y vendido en las 3 peores, igual peso, una semana.
- Criterio H1-H3: retornos diarios de la cartera **netos de costos** (0,125% por lado en cada
  cambio de posicion) se regresan contra la cartera de comprar y mantener las mismas monedas; hay
  ventaja si el **alfa** (lo que gana por encima de lo que explica el mercado) es positivo con
  **t >= 2,24** (errores Newey-West, 5 rezagos). Se informan ademas rendimiento anual, Sharpe y
  caida maxima frente a comprar y mantener.

- **H4 modelo LightGBM**: misma idea que en forex, adaptada. Cada 4 horas (al cierre de las velas
  de 00, 04, 08, 12, 16 y 20 UTC) predice si el precio sube o baja en las **24 horas** siguientes.
  Variables: retornos de 1h a 7 dias normalizados por volatilidad, volatilidad, rango y cuerpo de la
  vela, RSI, Bollinger, distancia a maximos/minimos de 7 dias, **volumen relativo** (24h frente a
  30 dias), **proporcion de compra agresiva** (1h y 24h), **operaciones relativas**, hora y dia,
  retornos de BTC y ETH en el mismo momento, y la moneda (one-hot). Mismos hiperparametros que en
  forex (300 arboles, aprendizaje 0,03, 31 hojas, min 500 por hoja, semilla 42).
  - Opera el 10% mas seguro (umbral de entrenamiento), largo o corto, una posicion a la vez por
    moneda, costo 0,25% ida y vuelta.
  - **Reserva 1**: reentreno anual, se mide 2021-01-01 a 2025-06-01 (entrenamiento desde 2017-09,
    1 dia de separacion).
  - **Reserva 2 (ciega, decisiva)**: 2025-06-01 a 2026-11-16, modelo entrenado con todo hasta
    2025-06-01.
  - Ventaja si en reserva 1 el retorno medio neto por operacion es positivo con z >= 2,24 **y** en
    reserva 2 el retorno medio neto es positivo.

## Secundarias (informativas, no deciden)

- H1-H3 con costos 0,05% y 0,20%, y por subperiodo (2019-2021, 2022-2026).
- H4 al 5% y 20%, por moneda, y solo largos.
- **Deriv multiplicadores**: las senales de H4 en BTC y ETH sobre velas de Deriv (2025-11-17 en
  adelante, dentro de la reserva 2), x100, stop out si el minimo/maximo de una vela de 1h toca el
  nivel de liquidacion, cierre a las 24h. Resultado por dolar de stake.
- Control de fuente: acuerdo de direccion de 1h entre Deriv y Binance en BTC y ETH.

## Que decide

- Alguna de H1-H4 con ventaja -> forward test en papel con los datos de Binance (sin dinero) y se
  evaluan plataformas (Binance, HTX/Huobi, Kraken, OKX...) por costos, seguridad y regulacion en
  Colombia. Las operaciones reales las hace el usuario.
- Ninguna con ventaja -> cripto con estos metodos queda descartado; no se ajustan parametros
  sobre estos datos.

## Resultado (2026-11-17)

Detalle en `cripto_2026-11-17.txt` / `.json`.

**H1-H3 (2019-01-01 a 2026-11-16, costo 0,125% por lado), cartera de igual peso:**

| | Anual | Sharpe | Caida max | Alfa anual | t | Veredicto |
|---|---|---|---|---|---|---|
| Comprar y mantener | 67,3% | 1,06 | 78,7% | - | - | referencia |
| **H1 tendencia (media 50, compra o fuera)** | 83,2% | 1,47 | 51,5% | 32,4% | **2,97** | **VENTAJA** |
| **H2 momentum de serie (largo/corto)** | 50,0% | 0,96 | 58,4% | 52,3% | **2,45** | **VENTAJA** |
| H3 momentum cruzado | 18,3% | 0,58 | 70,9% | 31,8% | 1,72 | sin ventaja |

Aguantan los costos de 0,05% y 0,20% por lado. **Pero casi toda la ventaja esta en 2019-2021**:
en 2022-2026 H1 tiene alfa +9,7% con t 0,80 y H2 +25,6% con t 1,16 (positivos, no significativos).

**H4 LightGBM: SIN VENTAJA.** Reserva 1 (2021-2025/05) al 10%: 57,4% de acierto, +0,40% neto por
operacion, z 4,11. Reserva ciega (2025/06 en adelante): 52,9%, **-0,11% neto**, z -1,03. Mismo
patron que en forex: brilla en el pasado y falla en datos nuevos.

**Secundarias:** Deriv multiplicadores x100 con las senales de H4: la mitad de las operaciones en
BTC y dos tercios en ETH terminan liquidadas; -0,02 y -0,06 USD por dolar. Control de fuente: Deriv
y Binance coinciden en direccion de 1h en 98,6% (BTC) y 99,1% (ETH).

**Robustez posterior (no decide, `scripts/ml/cripto_robustez.py`):** H1 con medias de 20 a 50 dias da t 2,6-3,0;
con 100-200 dias el alfa baja (t 0,9-1,5). Por moneda, H1 mejora el Sharpe en 6 de 9 (BTC t 2,64,
BNB 2,33, SOL 2,43, ETH 2,06) y empeora en LTC y XRP. Solo BTC+ETH desde 2022: alfa 22,5% (t 1,92),
Sharpe 0,81 frente a 0,25, caida maxima 41% frente a 68%.

**Advertencias:** (1) las 9 monedas se eligieron hoy, sabiendo cuales sobrevivieron y crecieron:
eso favorece al momentum; (2) la ventaja de la tendencia viene sobre todo de salirse antes de las
grandes caidas, y aun asi la caida maxima es de 40-50%; (3) desde 2022 no es significativa.

**Conclusion:** H1 y H2 pasan la regla pre-registrada. No son una maquina de ganar sino una forma
de invertir en cripto con menos caida que comprar y mantener, cuya ventaja reciente no esta
demostrada. Segun el plan, siguiente paso: forward test en papel y evaluacion de plataformas. El
modelo de IA (H4) queda descartado.
