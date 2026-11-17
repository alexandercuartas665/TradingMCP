# Cripto, lote 2: seis estrategias pensadas para cripto

Plan **pre-registrado** el 2026-11-17, antes de calcular ningun resultado de estas estrategias. Los
parametros quedan fijos; no se ajustan despues de ver datos.

## Por que

En el estudio anterior (`cripto_plan.md`) pasaron reglas de tendencia y momentum (H1 media de 50
dias, H2 momentum de 4 semanas), sobre todo por 2019-2021; el modelo de IA no. Las estrategias del
sistema se disenaron para binarias de 2 velas en forex; en cripto comprado directamente lo que paga
es el tamano del movimiento en dias o semanas. Este lote prueba seis ideas propias de cripto.

## Datos y reglas comunes

- Binance spot, 9 monedas (BTC, ETH, BNB, LTC, XRP, ADA, LINK, DOGE, SOL), velas diarias UTC
  armadas con las de 1h (cierre, maximo, minimo, volumen, volumen de compradores agresivos). Tasas
  de financiamiento de futuros perpetuos (suma diaria). Datos en `D:\traiding_ml\cripto\datos`.
- La senal se calcula al cierre del dia t y la posicion se toma el dia t+1. Cartera de igual peso
  entre las monedas con datos ese dia (cada moneda pesa 1/n). Costo 0,125% por lado en cada cambio
  de posicion. Los cortos (solo E6) suponen futuros perpetuos con el mismo costo; no se cuenta el
  financiamiento cobrado o pagado.
- **Diseno: 2019-01-01 a 2025-06-01. Reserva ciega: 2025-06-01 a 2026-11-16**, que solo se mira al
  final y una vez.

## Las seis estrategias (parametros fijos)

- **E1 Ruptura de canal 20/10 (compra o fuera)**: entra si el cierre supera el mayor cierre de los
  20 dias anteriores; sale si el cierre cae bajo el menor cierre de los 10 dias anteriores.
- **E2 Tendencia con tamano por volatilidad**: senal de H1 (cierre sobre su media de 50 dias), pero
  el peso de cada moneda es (1/n) x min(1; 50% / volatilidad anualizada de 30 dias).
- **E3 Filtro de BTC**: senal de H1 en cada moneda, y solo si BTC tambien esta sobre su media de 50.
- **E4 Ruptura con volumen**: como E1, pero solo entra si el dia de la ruptura el volumen supera
  1,5 veces su media de 20 dias y la compra agresiva es mas de la mitad del volumen. Salida de E1.
- **E5 Rebote tras panico (compra)**: si el retorno del dia es menor que -2,5 veces la desviacion
  diaria de 30 dias y el volumen supera 2 veces su media de 20 dias, comprado los 3 dias siguientes.
- **E6 Financiamiento extremo (largo/corto)**: suma de financiamiento de los ultimos 3 dias; si
  supera el percentil 95 de su propio ultimo ano -> vendido 3 dias; si queda bajo el percentil 5 ->
  comprado 3 dias. Necesita 1 ano de historia de financiamiento por moneda.

## Que decide

- Retornos diarios netos de cada cartera, regresados contra comprar y mantener las mismas monedas
  (errores Newey-West, 5 rezagos).
- **Ventaja en diseno**: alfa positivo con **t >= 2,39** (Bonferroni para 6, alfa 0,05/6 unilateral).
- **Confirmada**: ademas, alfa positivo en la reserva ciega.
- **Mejor que la tendencia simple**: se informa el Sharpe y la caida maxima de cada estrategia frente
  a H1 (media 50) en los dos periodos. Una estrategia confirmada que no mejore a H1 en Sharpe o en
  caida maxima en la reserva no aporta frente a lo que ya tenemos.

## Secundarias (informativas)

E1 con canal 55/20; costos 0,05% y 0,20%; resultado por moneda en diseno.

## Despues

Confirmadas -> entran al forward test en papel junto con H1 y H2. Ninguna confirmada -> se queda la
regla de tendencia simple como unica candidata.

## Resultado (2026-11-17)

Detalle en `cripto_lote2_2026-11-17.txt` / `.json`. Financiamiento disponible desde 2020 (DOGE y SOL
desde mediados de 2020) hasta 2026-10-31.

| Estrategia | Diseno: alfa (t) | Diseno: Sharpe / caida | Reserva: anual | Reserva: Sharpe / caida | Veredicto |
|---|---|---|---|---|---|
| H1 tendencia (referencia) | 33,4% (2,64) | 1,66 / 51,5% | +3,4% | 0,26 / 39,3% | - |
| E1 canal 20/10 | 34,5% (2,86) | 1,73 / 39,9% | -4,8% | -0,10 / 35,8% | confirmada, mejora caida |
| **E2 tendencia por volatilidad** | 19,7% (2,49) | 1,64 / 34,9% | **+7,3%** | **0,41 / 31,4%** | **confirmada, mejora a H1** |
| E3 filtro BTC | 43,1% (3,02) | 1,77 / 46,6% | +2,3% | 0,23 / 39,6% | confirmada, no mejora |
| E4 canal con volumen | 32,1% (3,01) | 1,76 / 28,1% | -10,8% | -0,59 / 31,1% | falla en la reserva |
| E5 rebote tras panico | -7,0% (-1,47) | - | +12,0% | 0,85 / 7,3% | sin ventaja |
| E6 financiamiento extremo | -7,9% (-1,00) | - | -3,4% | - | sin ventaja |
| Comprar y mantener | - | 1,32 / 78,7% | **-35,3%** | -0,46 / 66,9% | - |

**Lectura:**
- Las variantes de tendencia (E1, E2, E3) pasan el criterio pre-registrado, pero la reserva ciega
  fue un mercado bajista (comprar y mantener -35% al ano) y alli su alfa es positivo **sin
  significancia** (t 0,3 a 1,1). Lo que si se repite: **perdieron mucho menos que el mercado**
  (caidas de 31-40% frente a 67%).
- E2 (tendencia con tamano por volatilidad) es la mas consistente: menor caida en diseno y en
  reserva, unica con rendimiento positivo relevante en la reserva (+7,3%) y Sharpe mayor que H1.
- La ruptura con volumen (E4) brillaba en diseno y fallo en la reserva: otro caso de sobreajuste.
- Rebote tras panico y financiamiento no tienen ventaja (E5 positivo en la reserva con solo 3% del
  tiempo invertido; no cuenta).
- Siguen las advertencias del primer estudio: monedas elegidas con sesgo de supervivencia, y las
  cuatro variantes de tendencia son la misma idea, no cuatro confirmaciones independientes.

**Conclusion:** la familia de seguir tendencia es lo unico que se sostiene en cripto, y su valor
real es **proteger en las caidas**, no ganar mas. La mejor version es E2. Pasan al forward test en
papel: E2 (principal) con H1 como referencia.
