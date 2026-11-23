# Registro de ideas probadas

**Objetivo (decision del usuario, 2026-11-22):** seguir investigando en papel, sin dinero real y sin
prisa, para construir un metodo validado y, si aparece, una estrategia con evidencia para cuando haya mas
capital. Capital declarado hoy: USD 100.

Una fila por idea. Rutas relativas a `docs/validacion/` salvo que se diga otra cosa.
Columna "Plan": **previo** = plan en un commit anterior al resultado; **mismo commit** = plan y
resultado en el mismo commit (git no prueba el orden: se trata como exploratorio); **no** = sin
archivo de plan. "Sin archivo" = la cifra no esta en ningun archivo del repo.

## Estandar vigente (desde 2026-11-22)

- El plan va en un commit propio, subido ANTES de leer los datos del experimento. Lo que no cumpla
  eso es exploratorio, no evidencia.
- Conteo de pruebas para corregir: **N = 380** (cuenta alta; el "150 previas" de
  `WpfDerivClientVB/Infraestructura/ConfigManager.vb:60` es un valor fijo, no medido).
  Bonferroni al 5%: z >= 3,65 una cola, 3,82 dos colas.
- Hipotesis nueva: ademas, prueba de permutacion con la busqueda de parametros incluida (como E2).
- Ninguna cifra de memoria: si no hay archivo, se escribe "sin archivo".
- **Operable con USD 100** (capital declarado por el usuario el 2026-11-22): sin comision minima por
  orden, tamano minimo de orden de pocos dolares y pocos instrumentos a la vez. Un plan que no lo
  cumpla puede correrse como investigacion, pero no se le presenta al usuario como operable.

## Tabla

| # | Fecha | Idea (ventaja supuesta) | Mercado | Veredicto | Cifra clave | Resultado | Plan |
|---|---|---|---|---|---|---|---|
| 1 | 11-14 | Estrategias "minadas" del Heavy Tester (BollingerMomentum_v2, CCIReversal_v2...) ganan en binarias | Deriv 1h/4h, 10 config | SIN VENTAJA (sobreajuste) | filas por estrategia en el JSON; agregados 627 ops 60,8% diseno / 150 ops 48,0% ciego: sin archivo (commit 04d969c) | `validacion_2026-11-14.json` | no |
| 2 | 11-14 | Las mismas, en walk-forward 365/90 | Deriv, 6 config | SIN VENTAJA | CCIReversal_v2 69,6% contaminado -> 46,9% limpio (32 ops) | `robustez_resultados.json` | no |
| 3 | 11-14 | Las mismas en otros mercados | 4 estrategias x 22 mercados = 88 | SIN VENTAJA (0 de 88) | ciego 48,8-53,7% | `multimercado_resultados.json` | no |
| 4 | 11-14 | Las 4 estrategias como multiplicadores (mejor de 63 combinaciones cada una) | Deriv 2024-26 | SIN VENTAJA | BollingerMomentum_v2 ciego +0,177/USD IC -0,013/0,367 sin comision | `multiplicadores_resultados.json` | no |
| 5 | 11-14 | Operaciones reales de la app | R_100 5 ticks, demo | pierde | 189 ops 50,3%, -24,49 USD: sin archivo (tabla trading_operations de la BD) | sin archivo | no |
| 6 | 11-15 | LondresMomentum_v1: la primera hora de Londres con cuerpo fuerte continua | EUR/GBP 1h | SIN VENTAJA | EUR ciego 56,6% en 113 ops (no signif.); largo 2003-24 47,9% en 4.259 | `hipotesis_2026-11-15.txt`, fila 14-15 de `historico_largo_2026-11-15.txt` | no (registradas en la app antes de probar) |
| 7 | 11-15 | AsiaReversion_v1: Bollinger+RSI extremo en Asia revierte | EUR/GBP 1h (luego JPY/CHF/GBPJPY) | SIN VENTAJA | EUR 2003-24 60,1% en 303 ops z 2,10; 2024-26 40,0% | igual que #6; `lote2_hipotesis_2026-11-15.txt` idea 17 | no / previo (lote 2) |
| 8 | 11-15 | SobrerreaccionVela_v1: vela > 2 ATR corrige | EUR/GBP/JPY/XAU 1h | SIN VENTAJA | 2003-24: 50,1-51,4% | igual que #6 | no |
| 9 | 11-15 | Las 21 config anteriores (incl. #6-#8 y las del forward test) en historico largo | Dukascopy 2003-05 a 2024-03 | SIN VENTAJA (0 de 21) | 49.702 ops 51,00%; criterio z >= 2,82 | `historico_largo_2026-11-15.txt` | previo (`historico_largo_plan.md`) |
| 10 | 11-15 | Lote 2: 20 ideas en 61 pruebas (horarios, hueco del lunes, Donchian diario, momentum diario, RSI2 indices, velas, ADX, Ichimoku, cruce de medias, compresion...) | forex/oro/indices 2003-24 | SIN VENTAJA (0 de 20) | 169.989 ops 50,34%; mejores RSI2Indices 65,0% (140 ops, z 2,60), LunesGap 56,72% (z 1,96) | `lote2_hipotesis_2026-11-15.txt` | previo (`lote2_hipotesis_plan.md`) |
| 11 | 11-15 | Filtro de noticias NFP/FOMC mejora binarias / reduce riesgo en multiplicadores | 60 config 2003-24 | binarias SIN EFECTO; multiplicadores: bloquear ventanas | expuestas 50,38% vs 50,48% z -0,13; movimiento x1,62 | `filtro_noticias_2026-11-15.txt` | previo (`filtro_noticias_plan.md`) |
| 12 | 11-16 | Las 4 estrategias como multiplicadores con historico largo | 10-14 mercados 2003-24 | SIN VENTAJA | BollingerMomentum -0,0284/USD bruto z -2,12 | `multiplicadores_historico_largo_2026-11-16.txt` | mismo commit (4e28e5d) |
| 13 | 11-16 | Logistica (47 variables) predice la vela | forex/metales 1h | SIN VENTAJA | ver archivo | `modelo_ml_2026-11-16.txt` | mismo commit (4e28e5d) |
| 14 | 11-16 | LightGBM (47 variables) para binarias | 7 mercados 1h | "VENTAJA" por regla, NO operable | reserva 2 Deriv 10%: 55,47% en 7.226 ops z(85%) 2,41; equilibrio real 58% | `modelo_ml_2026-11-16.txt` | mismo commit (4e28e5d). Nota: los z probablemente inflados por mercados correlacionados (ver duda 4) |
| 15 | 11-16 | LightGBM para multiplicadores | 7 mercados 1h | SIN VENTAJA | ver archivo | `modelo_ml_2026-11-16.txt` | mismo commit (4e28e5d) |
| 16 | 11-16 | LSTM 48 velas para binarias | 7 mercados 1h | "VENTAJA, no operable" | reserva 2 Deriv 10%: 56,48% en 5.752 ops | `modelo_lstm_2026-11-16.txt` | previo (`modelo_lstm_plan.md`). Nota: duda 4 |
| 17 | 11-17 | H1 cripto: tendencia SMA50 (compra o fuera) | Binance 9 monedas 1d | VENTAJA en 2019-26; desde 2022 no signif. | alfa 32,41% t 2,97; 2022-26 t 0,80 | `cripto_2026-11-17.txt` | mismo commit (9b50a6a) |
| 18 | 11-17 | H2 cripto: momentum de serie | idem | VENTAJA | t 2,45 | `cripto_2026-11-17.txt` | mismo commit |
| 19 | 11-17 | H3 cripto: momentum cruzado | idem | SIN VENTAJA | t 1,72 | `cripto_2026-11-17.txt` | mismo commit |
| 20 | 11-17 | H4 cripto: LightGBM a 24h | idem 1h | SIN VENTAJA | reserva ciega 10% neto -0,111% z -1,03 | `cripto_2026-11-17.txt` | mismo commit |
| 21 | 11-17 | E1 canal 20/10 | Binance 9 monedas | "CONFIRMADA" | diseno t 2,86; reserva t 0,28 | `cripto_lote2_2026-11-17.txt` | mismo commit (8ba5def). Nota: duda 3 |
| 22 | 11-17 | E2 tendencia SMA50 + tamano por volatilidad | idem | "CONFIRMADA" | diseno t 2,49; reserva alfa 18,00% t 1,09 | `cripto_lote2_2026-11-17.txt` | mismo commit. Nota: duda 3 |
| 23 | 11-17 | E3 tendencia con filtro BTC | idem | "CONFIRMADA", no mejora a H1 | diseno t 3,02; reserva t 0,72 | `cripto_lote2_2026-11-17.txt` | mismo commit. Nota: duda 3 |
| 24 | 11-17 | E4 canal + volumen | idem | falla en reserva | diseno t 3,01; reserva t -0,51 | `cripto_lote2_2026-11-17.txt` | mismo commit |
| 25 | 11-17 | E5 rebote tras panico | idem | SIN VENTAJA | diseno t -1,47 | `cripto_lote2_2026-11-17.txt` | mismo commit |
| 26 | 11-17 | E6 financiamiento como senal de direccion | idem | SIN VENTAJA | diseno t -1,00 | `cripto_lote2_2026-11-17.txt` | mismo commit |
| 27 | 11-18 | Datos sinteticos mejoran H4 | Binance | NO ayudan | 0 de 5 semillas (S1 y S2) | `cripto_sintetico_2026-11-18.txt` | previo (`cripto_sintetico_placebo_plan.md`) |
| 28 | 11-18 | Placebo: las reglas de tendencia no son azar | 1.000 mercados de signo al azar | tendencia en percentil 99,7-100; z del modelo H4 inflado | E2 t 2,49 vs p95 placebo 1,05 | `cripto_sintetico_2026-11-18.txt` | previo |
| 29 | 11-18 | E2 en BTC fuera de muestra | Bitstamp 2012-2016 | FUNCIONA | alfa 49,71% t 3,37 | `e2_bitstamp_2026-11-18.txt` | previo (`e2_bitstamp_2012_2016_plan.md`, 2 min antes) |
| 30 | 11-19 | Brief: modelo-filtro LightGBM sobre estrategias base, triple barrera | Oro/Nasdaq/USDJPY H1 | NO HAY EVIDENCIA (los 3) | R medio -0,0607 / -0,0246 / -0,0247 | `brief_modelo_filtro_h1_2026-11-18.txt` | previo (`brief_modelo_filtro_plan.md`). M15 pendiente de datos |
| 31 | 11-21 | E2 contra permutaciones (4 portones) | Binance + Bitstamp | NO PASA (falla 2 y 4) | p 0,013 / 0,144; Bitstamp p 0,001 | `e2_permutacion_2026-11-21.txt` | previo (`e2_permutacion_plan.md`, 17 min antes) |

## Cola de hipotesis (sin datos mirados)

| Orden | Idea | Estado | Nota |
|---|---|---|---|
| 1 | TSMOM diversificado con tamano por volatilidad (P&L) | plan `tsmom_plan.md` (e453f72), enmienda 1 en curso | universo B cerrado hasta visto bueno del lider |
| - | Carry de base en cripto (spot contra perpetuo, neutral) | **APARCADA por rendimiento esperado insuficiente frente al riesgo, sin backtest** (2026-11-22) | Operable en tamano con USD 100 en Binance (minimos de 5 USDT; exchangeInfo 2026-11-22), pero el rendimiento publico esperado (BIS WP 1087 "Crypto carry": mas de 10% anual sobre la pata corta; arXiv 2510.14435: ~8%) da 4-5,5% anual sobre el capital total antes de los costos de entrada y salida del dinero, al nivel de la tasa libre de riesgo en USD (IBKR paga 3,38% en USD, 2026-11-22), con riesgos de cola que esa tasa no tiene (exchange, USDT, liquidacion). Ya visto: tasas de financiamiento de Binance de 9 monedas desde 2020, usadas como senal en E6. Se reabre solo si el usuario lo pide o si el financiamiento publico vuelve a niveles claramente superiores. Si se reabre, el plan declara desde el principio el capital minimo operable |
| 3 | Carry de divisas G10 con filtro de crisis | pendiente | depende de donde quiera operar el usuario |

## Forward tests en papel

| Prueba | Desde | Registro | Plan |
|---|---|---|---|
| LightGBM binarias forex | 2026-11-16 | `D:\traiding_ml\forward\` | `forward_ml_plan.md` |
| Cripto E2/H1 diario | 2026-11-18 | `D:\traiding_ml\forward_cripto\diario.jsonl` | `forward_cripto_plan.md` |
| IndicePullback/EmaRsi en demo (lo lanza el usuario) | no arrancado | - | `forward_test_plan.md` |

## Notas de las filas

- **Duda 3 (filas 21-23):** la reserva "ciega" 2025-06+ del lote 2 cripto no era ciega para la
  familia de tendencia. El estudio cripto del mismo dia ya habia medido H1 de 2019-01 a 2026-11
  (reserva incluida), y E1/E2/E3 son variantes de H1 disenadas despues. Las 9 monedas tienen sesgo
  de supervivencia. El "CONFIRMADA" vale menos de lo que dice.
- **Duda 4 (filas 14 y 16):** el placebo B1 (fila 28) mostro z inflados ~2x por monedas
  correlacionadas a la misma hora. Lo mismo deberia aplicar al ML de forex (7 mercados
  correlacionados frente al USD). No medido.
- Las binarias tempranas (filas 1-10) suponian payout 85%; el real es 72-79%: el sesgo favorece a
  las estrategias, asi que "sin ventaja" se sostiene.

## Conteo

Unas 70 ideas distintas; unas 290 pruebas con veredicto propio si las 88 de multimercado estan
dentro de las 150 previas, unas 380 si no. Se usa N = 380. Combinaciones de parametros exploradas:
mas de 600 (63 x 4 multiplicadores, rejilla de 80 de E2, Heavy Tester).
