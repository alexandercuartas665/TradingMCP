"""INFORMATIVO (no cambia el veredicto): desglose del principal de B, swaps de USDTHB/USDCNH y bruto por mitades.
Uso: python -I informativo_b.py <repo> <salida.txt>"""
import os
import sys

import numpy as np
import pandas as pd

sys.path.insert(0, os.path.join(sys.argv[1], 'scripts', 'ml'))
import tsmom as T

L = ['INFORMATIVO - no cambia el veredicto. Universo B, escenario principal (IBKR Pro USD 100.000, regla 252).', '']
todos, clt, _ = T.cargar('B')
ci, cl, co = T.sin_cmd(todos, clt, T.costos_por_mercado('B'))
ex = T.extra_conversion(todos)
res, ini, _ = T.correr_cartera(ci, cl, co, 'B', modo='ibkr', extra=ex)
sec, _, _ = T.correr_cartera(ci, cl, co, 'B', inicio=ini)

# ---- desglose: se repite la cuenta de resultado() por componente y se comprueba que suma el principal
w_obj, act, precios, cierre = T.objetivos(ci, cl, T.PRINCIPAL)
mn = T.minimos('B', 'ibkr')
vu = T.valor_unidad(precios, ex)
w = T.rebalanceo(w_obj, T.BANDA, mn, vu, T.CAPITAL, cierre if cierre.values.any() else None).shift(1).fillna(0.0)
idx = w.index
r = precios.pct_change().fillna(0.0)
dias = pd.Series(idx, index=idx).diff().dt.days.fillna(1).clip(lower=1)
f = dias / 365.0
TT = T.tasas(idx)
giro = w.diff().abs().fillna(w.abs())
comp = pd.DataFrame(0.0, index=idx, columns=['precio', 'carry', 'spread', 'comisiones', 'financiacion_ibkr', 'ajuste_usdthb'])
comp['precio'] = (w * r).sum(axis=1)
comp['spread'] = -(giro * pd.Series(co['medio'])).sum(axis=1)
for k in w.columns:
    wk = w[k]
    carry_k = pd.Series(0.0, index=idx)
    if T.es_divisa(k):
        b, q = T.monedas(k)
        carry_k = wk * ((TT[b] - TT[q]).fillna(0.0) / 100.0) * f
        comp['carry'] += carry_k
    if co['swaps'].get(k) is None:
        s_k = wk.abs() * T.RECARGO * f
    else:
        largo, corto = co['swaps'][k]
        s_k = -wk.abs() * (np.where(wk > 0, largo, corto) / precios[k]) * dias
    s_k = s_k.where(wk != 0, 0.0)
    ib = co['ib'][k]
    dw = giro[k]
    if ib == 'fx':
        b, q = T.monedas(k)
        lr = T._spread_tasa_ib(b, True, TT) + T._spread_tasa_ib(q, False, TT)
        cr = T._spread_tasa_ib(q, True, TT) + T._spread_tasa_ib(b, False, TT)
        comp['financiacion_ibkr'] -= wk.abs() * np.where(wk > 0, lr, cr) * f
        c = np.where(dw > 0, np.maximum(dw * T.IB_COMISION_FX, T.IB_MINIMO_FX / T.CAPITAL), 0.0)
        c += np.where((dw > 0) & (dw * T.CAPITAL < T.IB_LOTE_IMPAR_USD), dw * T.IB_LOTE_IMPAR_COSTO, 0.0)
        comp['comisiones'] -= c
    elif ib == 'etf':
        comp['financiacion_ibkr'] -= wk.abs() * np.where(wk > 0, T.IB_RECARGO_ETF[0], T.IB_RECARGO_ETF[1]) * f
        comp['comisiones'] -= np.where(dw > 0, np.minimum(np.maximum(dw * T.IB_COMISION_ETF, T.IB_MINIMO_ETF / T.CAPITAL), dw * 0.01), 0.0)
    elif ib == 'cfd':
        comp['financiacion_ibkr'] -= wk.abs() * T.IB_RECARGO_CFD * f
        comp['comisiones'] -= dw * co['com'][k]
    else:
        comp['ajuste_usdthb'] -= s_k + carry_k
comp = comp[comp.index >= ini]
dif = float((comp.sum(axis=1) - res['principal']).abs().max())
L.append('Comprobacion: la suma de los componentes reproduce el principal (diferencia maxima diaria %.2e).' % dif)
L.append('')
L.append('Desglose anual del principal (% del capital; suma de retornos diarios):')
cab = ['precio', 'carry', 'spread', 'comisiones', 'financiacion_ibkr', 'ajuste_usdthb']
L.append('  %-5s ' % 'ano' + ' '.join('%11s' % c[:11] for c in cab) + ' %9s' % 'total')
anual = comp.groupby(comp.index.year).sum() * 100
for a, fila in anual.iterrows():
    L.append('  %-5d ' % a + ' '.join('%11.2f' % fila[c] for c in cab) + ' %9.2f' % fila.sum())
tot = comp.sum() * 100 / (len(comp) / 252)
L.append('  media ' + ' '.join('%11.2f' % tot[c] for c in cab) + ' %9.2f' % tot.sum())
L.append('')

# ---- swaps de Deriv de USDTHB y USDCNH como % anual del nocional
fila = T.tabla_deriv()
L.append('Swap de la tabla de Deriv como % anual del nocional (swap por unidad de precio x 365 / precio):')
for k in ('USDTHB', 'USDCNH'):
    largo, corto = co['swaps'][k]
    p = ci[k]
    f_d = fila[k]
    for etiqueta, precio in (('ultimo cierre', float(p.iloc[-1])), ('mediana del periodo', float(p.median()))):
        L.append('  %s: puntos largo %s corto %s; punto = %g; a %s %.4f -> largo %.2f%%, corto %.2f%% (negativo = paga)'
                 % (k, f_d['swap_largo_pts'], f_d['swap_corto_pts'], 0.001 if k[3:] in ('JPY', 'THB') else 0.00001,
                    etiqueta, precio, 100 * largo * 365 / precio, 100 * corto * 365 / precio))
L.append('')

# ---- bruto (y principal) por mitades
L.append('Bruto y principal por mitades:')
for nombre, a, b in (('2007-03-15 a 2016-12-31', ini, pd.Timestamp('2016-12-31')),
                     ('2017-01-01 a 2026-11-20', pd.Timestamp('2017-01-01'), pd.Timestamp(T.FIN))):
    for esc, s in (('bruto', sec['bruto']), ('solo precio', sec['solo_precio']), ('principal', res['principal'])):
        m = T.metricas(s[(s.index >= a) & (s.index <= b)])
        L.append('  %-24s %-12s t %6.2f | sharpe %5.2f | anual %6.2f%% | vol %5.2f%%'
                 % (nombre, esc, m['t_nw'], m['sharpe'], 100 * m['anual'], 100 * m['vol']))
texto = '\n'.join(L)
print(texto)
with open(sys.argv[2], 'w', encoding='utf-8', newline='\n') as fh:
    fh.write(texto + '\n')
