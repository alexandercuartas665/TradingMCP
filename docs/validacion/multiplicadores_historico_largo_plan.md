# Multiplicadores con historico largo (Dukascopy 2003-2024)

Plan **pre-registrado** el 2026-11-16, antes de correr la prueba. Las reglas no se tocan
despues de ver los resultados.

## Por que

La prueba de multiplicadores del 2026-11-14 (`multiplicadores_resultados.json`) uso solo velas
de Deriv (unos dos anos). BollingerMomentum_v2 quedo en +0,177 USD por cada dolar en el periodo
ciego (IC -0,013 a +0,367): prometedor pero no demostrado. Las velas Dukascopy de
**2003-05-01 a 2024-03-31** nunca se usaron para disenar ni para elegir configuraciones de
multiplicadores: son 21 anos fuera de muestra.

## Que se prueba (fijo)

- Las 4 estrategias de la prueba anterior: BollingerMomentum_v2 (conf 50), IndicePullbackLong_v1
  (50), CCIReversal_v2 (55), EmaRsiCruce_v4 (55).
- Mercados: los 7 de divisas y metales (EUR/USD, GBP/JPY, GBP/USD, USD/CHF, USD/JPY, oro, plata)
  a 1h y 4h. R_100 no entra: solo existe en Deriv.
- Stake 1 (resultados por dolar), una posicion a la vez, entrada al cierre de la vela. Las
  operaciones que cruzan un hueco de datos (fin de semana) se descartan, igual que antes.

## Prueba A (principal): configuracion fija

- En cada estrategia y mercado se usa **la misma configuracion** (multiplicador, TP, SL, velas
  maximas) que se eligio en el periodo de diseno de Deriv el 2026-11-14. No se elige nada nuevo.
  Mercados sin configuracion en esa prueba (menos de 30 operaciones en diseno) quedan fuera.
  Se usan todas las configuraciones elegidas, tambien las que no fueron rentables en diseno
  (igual que el total ciego de la prueba anterior).
- Periodo limpio: 2003-05-01 a 2024-04-01 (Dukascopy).
- Comision (fraccion del nocional = stake x multiplicador): la de divisas en Deriv aun no se ha
  medido. Se corre con **0 (bruto), 0,01%, 0,02% y 0,04%**.
- **Ventaja** si, sumando todos los mercados de la estrategia, el resultado medio por operacion
  en bruto tiene **z >= 2,24** (Bonferroni para 4 estrategias, alfa 0,05/4 unilateral)
  **y** es positivo en mas de la mitad de los mercados. Para las que pasen se informa hasta que
  comision aguantan (el mayor nivel con media > 0).
- Control de la fuente: la misma configuracion en 2024-04-01 a 2026-04-29 con Dukascopy frente
  al resultado de diseno con Deriv en ese mismo periodo. Si difieren mucho, se avisa.

## Prueba B (secundaria): eleccion por walk-forward dentro de Dukascopy

- Se repite el metodo de la prueba anterior (la mejor de 63 combinaciones por mercado, minimo
  30 operaciones) eligiendo en **2003-05-01 a 2014-01-01** y midiendo en **2014-01-01 a
  2024-04-01**. Solo en bruto. Mide si el metodo de elegir configuracion funciona, no una
  configuracion concreta. Mismo criterio de ventaja que A sobre el periodo de medicion.

## Que decide

- Ventaja en A -> se mide la comision real de divisas con el mercado abierto; si queda por
  debajo de la comision que aguanta, la estrategia pasa a demo con multiplicadores (con el
  filtro de noticias NFP/FOMC). Si B tambien da ventaja, se refuerza.
- Sin ventaja en A -> los multiplicadores no tienen ventaja demostrada con estas estrategias; no
  se ajustan parametros sobre estos datos.

## Resultado (2026-11-16)

Detalle en `multiplicadores_historico_largo_2026-11-16.txt` / `.json`.

**Prueba A (configuracion fija, 2003-2024), resultado medio por dolar:**

| Estrategia | Ops | Bruto (IC 95%) | z | Mercados + | Con 0,02% |
|---|---|---|---|---|---|
| BollingerMomentum_v2 | 9.091 | -0,028 (-0,055 / -0,002) | -2,12 | 4/10 | -0,109 |
| IndicePullbackLong_v1 | 19.017 | -0,034 (-0,047 / -0,021) | -5,16 | 5/14 | -0,122 |
| CCIReversal_v2 | 26.768 | -0,061 (-0,078 / -0,044) | -7,10 | 1/14 | -0,125 |
| EmaRsiCruce_v4 | 33.870 | -0,028 (-0,039 / -0,018) | -5,23 | 8/14 | -0,113 |

**Ninguna tiene ventaja: las cuatro pierden incluso sin comision.** El control de la fuente
cuadra (BollingerMomentum en 2024-04 a 2026-04: +0,116 con Dukascopy frente a +0,091 con Deriv),
asi que la diferencia no es de los datos: la ganancia de la prueba anterior era propia de esos
dos anos.

**Prueba B (elegir en 2003-2013, medir en 2014-2024, bruto):** BollingerMomentum -0,146 (z -8,9),
IndicePullback +0,002 (z 0,2), CCIReversal -0,023 (z -2,9), EmaRsiCruce +0,006 (z 1,1). Elegir la
mejor combinacion en el pasado no da ventaja en el futuro.

Comision real medida el 2026-11-16 (`payouts_reales.json`): EUR/USD 0,023%, GBP/JPY 0,023%,
GBP/USD 0,020%, USD/CHF 0,035%, USD/JPY 0,028%, oro 0,019%, plata 0,087% del nocional. Con
esas comisiones las perdidas son de 0,10-0,15 USD por dolar apostado.

**Conclusion: los multiplicadores con estas estrategias quedan descartados.**
