# TSMOM diversificado en el universo B: informe de cierre (2026-11-26)

Plan: `tsmom_plan.md` (e453f72) con las enmiendas 1 (c835d3d), 2 (2bf62c4) y 3 (85f560a).
Revisado y aceptado por la sesion TRADING.lider. **TSMOM queda cerrado; no se reabre con variantes.**

## Resumen

Cifras que encabezan el resultado (no dependen de la errata de abajo):

| Escenario (universo B, regla 252, 2007-03-15 a 2026-11-20) | t Newey-West | Anual | Archivo |
|---|---|---|---|
| Bruto (spread + carry) | -0,86 | -0,85% | `tsmom_B_corrida2_2026-11-25.txt` |
| Solo precio | -0,80 | -0,80% | `tsmom_B_corrida2_2026-11-25.txt` |

- **Pregunta 1** (existe la prima neta a escala, IBKR Pro USD 100.000): **SIN EVIDENCIA**.
  - El principal es el bruto menos comisiones y financiacion, asi que no puede llegar a t 2,0 si el
    bruto da -0,86.
- **Pregunta 2** (la puede capturar la cuenta del usuario, USD 100): **SIN EVIDENCIA** en IBKR; en Deriv
  **NO OPERABLE** (0 de 50 mercados con alguna posicion distinta de 0).
- **Veredicto para el usuario: SIN EVIDENCIA.**
- Como dice la expectativa del plan: la prima tampoco aparece en bruto, asi que se cierra la familia
  de tendencia en estos mercados.
- Principal IBKR USD 100.000: t -6,44, -5,40% al ano. **Esta cifra tiene una errata (ver abajo)**.
  No se cita como "el resultado".
- La permutacion dio p = 0,027 (informativo): el real y los barajados estan todos hundidos por los
  costos, asi que no es una senal.

## Corridas

1. **Primera corrida** (commit d505141, `tsmom_B_2026-11-25.txt` / `.json`, script de la enmienda 2):
   - el t salio nan en el principal y en otros escenarios con costos de IBKR o swaps de Deriv;
   - causa: el swap de USDTHB y USDCNH daba NaN antes de su primer cierre aunque el peso fuera 0.
   - Se queda en el repo sin tocar.
2. **Segunda corrida** (commit bb47f3d, `tsmom_B_corrida2_2026-11-25.txt` / `.json`):
   - script de la enmienda 3 (2ebde5b): swap 0 donde el peso es 0, mas una guarda contra NaN;
   - mismo comando, semilla 1, `--perm 1000`; 307,1 minutos, sin relanzar;
   - se lanzo despues de ver las cifras validas de la primera corrida, como declara la enmienda 3.

## ERRATA: error de unidades en el swap de USDTHB

Encontrada en el informativo posterior al resultado (commit 5933fb4,
`tsmom_B_informativo_2026-11-26.txt` y `tsmom_B_informativo_usdthb_2026-11-26.txt`).

- **Escenarios contaminados:**
  - principal IBKR USD 100.000 (t -6,44; -5,40%/ano);
  - costos x2;
  - IBKR a escala;
  - IBKR USD 10.000 e IBKR USD 100;
  - tabla de swaps de Deriv.
- **No los toca:** bruto, solo precio y los escenarios Deriv CFD (usan el recargo fijo, no la tabla
  de swaps).
- **Que pasa:**
  - IBKR no lista THB, asi que en los escenarios IBKR la posicion de USDTHB usa el swap de la tabla de
    Deriv.
  - Con la convencion del script (punto = 0,001 para THB, dividido por el precio de los datos), ese
    swap sale en -217% al ano del nocional en largo y -170% en corto.
  - El precio de USDTHB que trae Dukascopy esta **10 veces arriba** del real: va de 297 a 383 (ultimo
    339,55), cuando el real anda por 30-38.
  - Hay un error de unidades en la escala del precio, en el tamano del punto o en ambos. Sin la
    especificacion del contrato de Deriv no se sabe cual.
- **Cuanto pesa:**
  - el ajuste de USDTHB le resta al principal unos 2,4 puntos al ano de media (2,39);
  - desde 2018, entre 4 y 8 puntos al ano.
- **Cifra de referencia sin ese ajuste:** alrededor de -3,0% al ano.
  - Es informativa: se obtiene restando el componente del desglose.
  - No se recalculo el t ni la permutacion.
- **Decision del lider:**
  - no hay enmienda 4 ni tercera corrida;
  - el veredicto no depende de esto (bruto t -0,86, solo precio t -0,80) y otra corrida de 5 horas no
    cambia ninguna decision.
- **Por que no se detecto antes:**
  - A no tiene USDTHB;
  - la comprobacion de unidades de los swaps no se hizo por mercado antes de congelar el script.
  - Leccion agregada al estandar de pre-registro en `REGISTRO_IDEAS.md`.

## Informativo (no cambia el veredicto)

`tsmom_B_informativo_2026-11-26.txt`, escenario principal, media anual en % del capital:

| Componente | %/ano |
|---|---|
| precio | -0,47 |
| carry | +0,12 |
| spread | -0,17 |
| comisiones | -0,47 |
| financiacion IBKR | -2,02 |
| ajuste USDTHB (errata) | -2,39 |
| total | -5,40 |

- La suma de los componentes reproduce el principal (diferencia diaria maxima 1,7e-18).
- La financiacion de IBKR (2,0 puntos con nocional bruto medio 0,84) y las comisiones (0,47) son
  coherentes con la enmienda 1.
  - `_spread_tasa_ib` solo suma los diferenciales de la enmienda 1: lo que se pierde en la moneda que
    se tiene y lo que se paga en la que se debe.
  - Unica excepcion: CNH tenido, que pierde (tasa - 0,5%) porque IBKR paga 0,50% fijo.
- USDCNH en la tabla de Deriv: +0,12% largo, -0,47% corto al ano (razonable).
- Por mitades:

| Tramo | Bruto | Solo precio | Principal (con errata desde 2018) |
|---|---|---|---|
| 2007-03-15 a 2016-12-31 | t -1,02 (-1,47%/ano) | t -1,03 | t -2,77 (-3,73%/ano) |
| 2017-01-01 a 2026-11-20 | t -0,18 (-0,25%/ano) | t -0,08 | t -7,05 (-7,06%/ano) |

- El -7% al ano del segundo tramo no viene del precio. Viene del ajuste de USDTHB mas la financiacion
  y las comisiones.

## Limites

- **Financiacion de CFD:** en indices y oro el largo paga referencia + 1,5% en IBKR, pero el script
  cobra solo el 1,5% (se omite la referencia).
  - En indices lo compensan en parte los dividendos omitidos; en oro no.
  - Sesgo a favor de la estrategia: no cambia un "sin evidencia".
- **Precios de B vistos antes de la enmienda 2** (dias sueltos, sin ningun retorno de cartera):
  - XLE y XLK alrededor del 2025-12-05;
  - EURMXN y EURZAR alrededor del hueco 2016-2024;
  - la basura de USDZAR en 2003, Brent el 2020-03-09 y JPN el 2013-05-21.
- **Peso visto:** en la prueba del cierre forzado se vio un peso de cartera de B, EURMXN el 2016-05-19
  (largo, 0,9% del capital).
- **Los del plan y las enmiendas 1 y 2 siguen:**
  - Dukascopy no es Deriv;
  - spreads de IBKR aproximados con los de Deriv;
  - prestamo de ETF en corto supuesto (2%);
  - CNH con la tasa de China;
  - dividendos sin ajustar;
  - aceptacion de residentes de Colombia en IBKR sin confirmar.
- **Errata de USDTHB:** ver arriba. Es una errata y no un limite.
