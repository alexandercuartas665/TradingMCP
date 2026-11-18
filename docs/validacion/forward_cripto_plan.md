# Forward test en papel de la tendencia en cripto (E2 y H1)

Plan **pre-registrado** el 2026-11-18, antes del primer dia contado. No se cambian las reglas ni los
parametros mientras dure la prueba. No hay dinero: es un registro en papel.

## Por que

En `cripto_lote2_plan.md` la tendencia con tamano por volatilidad (E2) fue la estrategia mas
consistente (menor caida en diseno y en la reserva ciega, Sharpe mayor que H1). Su ventaja reciente
no es estadisticamente segura. El forward mide con dias que todavia no existen.

## Que se mide (fijo)

- Binance spot, velas diarias UTC (API publica `data-api.binance.vision/api/v3/klines`, intervalo
  1d), 9 monedas: BTC, ETH, BNB, LTC, XRP, ADA, LINK, DOGE, SOL contra USDT. Solo velas cerradas.
- Al cierre del dia d (00:00 UTC del dia siguiente, 7 pm en Colombia) se calcula, por moneda:
  - senal de tendencia: cierre de d sobre su media de 50 dias (incluido d);
  - volatilidad anualizada de 30 dias: desviacion de los 30 ultimos retornos diarios x raiz(365).
- Pesos para el dia d+1 (n = monedas con datos, hoy 9):
  - **E2 (principal)**: senal x min(1; 50% / volatilidad) / n.
  - **H1 (referencia)**: senal / n.
  - **Comprar y mantener (referencia)**: 1/n en todas.
- Resultado del dia: suma de peso x retorno de cada moneda, menos 0,125% por cada unidad de cambio de
  peso (igual que el backtest: rebalanceo diario a los pesos objetivo, lo no invertido queda en USDT
  sin rendimiento).
- Si el PC estuvo apagado, al volver se recuperan los dias pendientes con las velas cerradas (las
  senales son deterministas; se marcan como recuperados).

## Que decide

El forward no puede demostrar ventaja en meses: en el backtest hicieron falta anos. Sirve para:

1. Verificar que la regla se ejecuta en vivo igual que en el backtest.
2. Ver como se comporta en dias que nadie vio.

Revisiones a los **6 y 12 meses** (2027-05-18 y 2027-11-18): rendimiento, Sharpe y caida maxima de
E2, H1 y comprar y mantener.

- **Alarma** (E2 deja de ser candidata): caida maxima de E2 mayor que la de comprar y mantener, o
  mayor de 40% (la peor en diseno fue 34,9%).
- Sin alarma a los 12 meses -> E2 sigue como candidata. Cualquier uso con dinero lo decide el
  usuario; esto no es asesoria financiera.

## Como se corre

```
pwsh -File scripts\ml\forward_cripto_inicio.ps1 -Accion instalar   # inicio automatico + arrancar
pwsh -File scripts\ml\forward_cripto_inicio.ps1 -Accion estado     # proceso + informe
D:\traiding_ml\venv\Scripts\python.exe -I scripts\ml\forward_cripto.py --resumen
```

Registro en `D:\traiding_ml\forward_cripto\diario.jsonl` y `forward_cripto.log`.
