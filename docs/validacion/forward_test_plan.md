# Forward test en demo: frxUSDJPY 1h y frxGBPUSD 1h

Plan **pre-registrado** el 2026-11-14, antes de ver un solo dato del periodo de prueba.
Fijar las reglas antes de empezar es lo que evita repetir el sobreajuste: si cambian
durante la prueba, el conteo vuelve a cero.

## Por qué estas pistas

En la prueba multimercado (88 combinaciones, periodo ciego 2026-04-30 a 2026-11-14)
ninguna combinación fue significativa, pero frxUSDJPY 1h y frxGBPUSD 1h salieron por
encima del equilibrio con varias estrategias a la vez:

| Estrategia | Mercado | Ops ciego | Acierto ciego |
|---|---|---|---|
| IndicePullbackLong_v1 | frxUSDJPY 1h | 66 | 65,2% |
| IndicePullbackLong_v1 | frxGBPUSD 1h | 65 | 63,1% |
| EmaRsiCruce_v4 | frxGBPUSD 1h | 121 | 61,2% |
| EmaRsiCruce_v4 | frxUSDJPY 1h | 111 | 59,5% |

Con 88 pruebas es esperable que algunas salgan bien por azar. Lo más probable es que
esto sea ruido. Por eso se prueba con datos futuros y sin tocar nada.

## Qué se prueba (fijo)

| Sesión | Estrategia | Mercado | Confianza mínima | Duración |
|---|---|---|---|---|
| 1 | IndicePullbackLong_v1 | frxUSDJPY 1h | 50 | 2 velas (120 min) |
| 2 | IndicePullbackLong_v1 | frxGBPUSD 1h | 50 | 2 velas (120 min) |
| 3 | EmaRsiCruce_v4 | frxUSDJPY 1h | 55 | 2 velas (120 min) |
| 4 | EmaRsiCruce_v4 | frxGBPUSD 1h | 55 | 2 velas (120 min) |

- Cuenta **DEMO**, 1 USD por operación, contrato Rise/Fall (CALL/PUT) de 120 minutos.
- Igual que el backtest validado: entrada al cierre de la vela de la señal y una sola
  posición abierta por sesión.
- Una vela que llega con más de 3 minutos de retraso (reconexión) no se opera.

## Cómo se mide

- Solo cuentan las operaciones con resultado de Deriv (Ganado/Perdido). Las no
  ejecutadas (p. ej. Deriv no deja vencer contratos entre 21:50 y 23:00) o bloqueadas
  por el gestor de riesgo se informan aparte.
- El mínimo para no perder se calcula con el **payout real** que pagó Deriv en las
  operaciones ganadas: `1 / (1 + payout)`. No se usa el 85% supuesto.
- Se mide el total de las 4 sesiones (la hipótesis es "estas dos divisas en 1h").
  Para afirmar algo de una sesión sola se exige un IC al 98,75% (corrección por 4 pruebas).

## Criterios de decisión

- **Duración**: hasta 300 operaciones en total o 6 meses, lo que llegue antes.
  Al ritmo del periodo ciego son unas 55 operaciones al mes.
- **Ventaja**: el límite inferior del IC95 del acierto total supera el mínimo para no perder.
- **Sin ventaja**: cualquier otro resultado al terminar.
- **Corte anticipado**: con 150 operaciones o más, si el límite superior del IC95 ya está
  por debajo del mínimo, se detiene: no hay ventaja.
- Aunque salga "ventaja", el siguiente paso sería otra prueba en demo con datos nuevos,
  no dinero real.

## Operación

```
pwsh -File scripts\live\forward_test.ps1 -Accion iniciar    # una vez, con la app abierta
pwsh -File scripts\live\forward_test.ps1 -Accion estado     # seguimiento
pwsh -File scripts\live\forward_test.ps1 -Accion reanudar   # tras reiniciar la app o el PC
pwsh -File scripts\live\forward_test.ps1 -Accion detener    # al terminar
```

La app debe quedar abierta (`WpfDerivClientVB.exe --heavy-api --minimized`) y conectada a la demo.
Si el PC se apaga, las velas de ese tiempo se pierden; al volver, `reanudar`.
