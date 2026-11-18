"""E2 en BTC con Bitstamp 2012-2016: prueba fuera de muestra pre-registrada.

Plan: docs/validacion/e2_bitstamp_2012_2016_plan.md (regla identica a la del forward, un solo activo).
Lee las velas de la base de la app con velas_bd (fuentes Bitstamp y Binance).

Uso (entorno de D:):
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/e2_bitstamp.py docs/validacion/e2_bitstamp_2026-11-18
"""
import datetime as dt
import json
import math
import os
import sys

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)

import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402

import cripto_estudio as C  # noqa: E402
import velas_bd  # noqa: E402

DESDE, HASTA = C.ep(2012), C.ep(2017)
T_CRITICO = 1.645
COSTOS_SENS = [0.0005, 0.002]


def serie(fuente):
    """Cierres diarios en calendario completo (dia sin operaciones = cierre anterior)."""
    d = velas_bd.velas(fuente, 'cryBTCUSD', '1d')
    s = d.set_index('epoch')['c']
    return s.reindex(range(int(s.index.min()), int(s.index.max()) + 1, 86400)).ffill()


def posiciones(c):
    r = c.pct_change()
    sma = c.rolling(50).mean()
    sig = (c > sma).astype(float).where(sma.notna())
    vol = r.rolling(30).std()
    return r, {'E2': (sig * np.minimum(1.0, 0.5 / (vol * math.sqrt(365)))).shift(1), 'H1': sig.shift(1)}


def neto(r, w, costo):
    w = w.fillna(0.0)
    return w * r.fillna(0.0) - w.diff().abs().fillna(0.0) * costo


def tramo(s, a, b):
    return s[(s.index >= a) & (s.index < b)]


def evaluar(r, w, costo, a, b):
    y, x = tramo(neto(r, w, costo), a, b), tramo(r, a, b)
    m = x.notna()
    alfa, t, beta = C.newey_west_alfa(y[m].to_numpy(), x[m].to_numpy())
    exp = float((tramo(w, a, b).fillna(0) != 0).mean())
    return dict(C.metricas(y[m].to_numpy()), alfa_anual_pct=round(alfa * 365 * 100, 2), t_alfa=round(t, 3),
                beta=round(beta, 3), exposicion=round(exp, 3))


def fecha(e):
    return str(dt.datetime.fromtimestamp(int(e), dt.timezone.utc).date())


def main(salida):
    c = serie('Bitstamp')
    r, P = posiciones(c)
    dias_sin_op = int((velas_bd.velas('Bitstamp', 'cryBTCUSD', '1d', DESDE, HASTA)['epoch'].nunique()))
    J = {'plan': 'docs/validacion/e2_bitstamp_2012_2016_plan.md', 'datos_desde': fecha(c.index.min()),
         'tramo': [fecha(DESDE), fecha(HASTA - 86400)], 'dias_con_operaciones': dias_sin_op}
    L = ['E2 EN BTC CON BITSTAMP 2012-2016 (fuera de muestra) - plan pre-registrado en ' + J['plan'],
         'Datos Bitstamp desde %s; tramo %s a %s; dias con operaciones %d de %d.' % (
             J['datos_desde'], J['tramo'][0], J['tramo'][1], dias_sin_op, (HASTA - DESDE) // 86400),
         'Costo 0,125%% por lado. Funciona si alfa anual > 0 y t >= %.3f (una cola 5%%).' % T_CRITICO, '']
    res = {k: evaluar(r, P[k], C.COSTO, DESDE, HASTA) for k in ('E2', 'H1')}
    bh = C.metricas(tramo(r, DESDE, HASTA).dropna().to_numpy())
    J['resultado'], J['comprar_mantener'] = res, bh
    L.append('== 2012-2016')
    for k, v in res.items():
        L.append('  %-18s anual %9.2f%%  sharpe %6.3f  caida %6.2f%%  expuesto %4.0f%% | alfa %8.2f%% t %5.2f beta %5.2f' % (
            k, v['retorno_anual_pct'], v['sharpe'], v['caida_max_pct'], v['exposicion'] * 100, v['alfa_anual_pct'], v['t_alfa'], v['beta']))
    L.append('  %-18s anual %9.2f%%  sharpe %6.3f  caida %6.2f%%' % ('comprar y mantener', bh['retorno_anual_pct'], bh['sharpe'], bh['caida_max_pct']))
    e2 = res['E2']
    if e2['alfa_anual_pct'] > 0 and e2['t_alfa'] >= T_CRITICO:
        v = 'FUNCIONA fuera de muestra'
    elif e2['alfa_anual_pct'] > 0:
        v = 'va en la direccion correcta, sin evidencia suficiente'
    else:
        v = 'no funciona en 2012-2016'
    J['veredicto'] = v
    L += ['', '== VEREDICTO (criterio principal): E2 ' + v, '', '== SECUNDARIAS (informativas)', '  por ano (retorno | caida maxima):']
    J['por_ano'] = {}
    for a in range(2012, 2017):
        fila = {}
        for k in ('E2', 'H1'):
            m = C.metricas(tramo(neto(r, P[k], C.COSTO), C.ep(a), C.ep(a + 1)).to_numpy())
            fila[k] = [m['multiplica_x'], m['caida_max_pct']]
        m = C.metricas(tramo(r, C.ep(a), C.ep(a + 1)).dropna().to_numpy())
        fila['BH'] = [m['multiplica_x'], m['caida_max_pct']]
        J['por_ano'][a] = fila
        L.append('    %d  ' % a + '   '.join('%s %+8.1f%% | %5.1f%%' % (k, (x[0] - 1) * 100, x[1]) for k, x in fila.items()))
    J['costos'] = {}
    for co in COSTOS_SENS:
        e = evaluar(r, P['E2'], co, DESDE, HASTA)
        J['costos'][co] = e
        L.append('  costo %.2f%% por lado: E2 alfa %.2f%% t %.2f sharpe %.3f' % (co * 100, e['alfa_anual_pct'], e['t_alfa'], e['sharpe']))
    # control de la fuente: E2 con Bitstamp vs con Binance donde se solapan
    cb = serie('Binance')
    rb, Pb = posiciones(cb)
    a, b = C.ep(2017, 11), int(min(c.index.max(), cb.index.max())) + 86400
    ys, yb = tramo(neto(r, P['E2'], C.COSTO), a, b), tramo(neto(rb, Pb['E2'], C.COSTO), a, b)
    comun = ys.index.intersection(yb.index)
    ms, mb = C.metricas(ys[comun].to_numpy()), C.metricas(yb[comun].to_numpy())
    J['control_fuente'] = {'desde': fecha(a), 'hasta': fecha(b - 86400), 'dias': len(comun),
                           'correlacion': round(float(np.corrcoef(ys[comun], yb[comun])[0, 1]), 4),
                           'bitstamp': ms, 'binance': mb}
    L.append('  control de fuente %s a %s (%d dias): correlacion diaria E2 Bitstamp/Binance %.4f; anual %.2f%% vs %.2f%%; sharpe %.3f vs %.3f' % (
        fecha(a), fecha(b - 86400), len(comun), J['control_fuente']['correlacion'], ms['retorno_anual_pct'], mb['retorno_anual_pct'], ms['sharpe'], mb['sharpe']))
    open(salida + '.txt', 'w', encoding='utf-8').write('\n'.join(L) + '\n')
    json.dump(J, open(salida + '.json', 'w', encoding='utf-8'), indent=1, default=float)
    print('\n'.join(L))


if __name__ == '__main__':
    main(sys.argv[1])
