"""Cripto lote 2: seis estrategias propias de cripto (plan pre-registrado docs/validacion/cripto_lote2_plan.md).

Uso (entorno de D:):
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/cripto_lote2.py D:/traiding_ml docs/validacion/cripto_lote2_2026-11-17
"""
import json
import math
import os
import sys
import time
import datetime as dt

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)

import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402

import cripto_estudio as C  # noqa: E402

T_CRITICO = 2.39
DISENO = (C.ep(2019), C.ep(2025, 6))
COSTOS_SENS = [0.0005, 0.002]


def diarias(h):
    d = h.assign(dia=h['epoch'] // 86400 * 86400).groupby('dia').agg(
        c=('c', 'last'), h=('h', 'max'), l=('l', 'min'), v=('volumen', 'sum'), tb=('compra_agresiva', 'sum'), n=('c', 'size'))
    return d[d['n'] >= 20]


def funding_diario(carpeta, sym):
    ruta = os.path.join(carpeta, 'cripto', 'datos', 'Binance_%s_funding.csv' % sym)
    if not os.path.exists(ruta):
        return pd.Series(dtype=float)
    f = pd.read_csv(ruta)
    if f.empty:
        return pd.Series(dtype=float)
    return f.assign(dia=f['epoch'] // 86400 * 86400).groupby('dia')['tasa'].sum()


def canal(c, v, tb, n_ent, n_sal, volumen=False):
    """Ruptura de canal (compra o fuera). Devuelve senal al cierre de cada dia."""
    max_prev = c.shift(1).rolling(n_ent).max()
    min_prev = c.shift(1).rolling(n_sal).min()
    v_med = v.shift(1).rolling(20).mean()
    cv, mx, mn, vv, vm, tt = c.to_numpy(), max_prev.to_numpy(), min_prev.to_numpy(), v.to_numpy(), v_med.to_numpy(), tb.to_numpy()
    out = np.full(len(cv), np.nan)
    dentro = False
    for i in range(len(cv)):
        if np.isnan(mx[i]) or np.isnan(mn[i]) or np.isnan(cv[i]):
            continue
        if not dentro and cv[i] > mx[i]:
            ok = True
            if volumen:
                ok = (not np.isnan(vm[i])) and vv[i] > 1.5 * vm[i] and vv[i] > 0 and tt[i] / vv[i] > 0.5
            dentro = ok
        elif dentro and cv[i] < mn[i]:
            dentro = False
        out[i] = 1.0 if dentro else 0.0
    return pd.Series(out, index=c.index)


def mantener(disparo, dias=3):
    """disparo: serie (+1, -1 o 0) al cierre del dia t -> posicion en t+1..t+dias."""
    d = disparo.to_numpy()
    out = np.zeros(len(d))
    for i in range(len(d)):
        if d[i] != 0 and not np.isnan(d[i]):
            out[i + 1:i + 1 + dias] = d[i]
    s = pd.Series(out, index=disparo.index)
    return s.where(disparo.notna() | (s != 0))


def posiciones(D, F):
    """Devuelve {estrategia: DataFrame dia x moneda} con la posicion YA desplazada (se aplica al dia)."""
    C_ = pd.DataFrame({s: d['c'] for s, d in D.items()})
    V = pd.DataFrame({s: d['v'] for s, d in D.items()}).reindex(C_.index)
    TB = pd.DataFrame({s: d['tb'] for s, d in D.items()}).reindex(C_.index)
    r = C_.pct_change()
    sma = C_.rolling(50).mean()
    sig50 = (C_ > sma).astype(float).where(sma.notna())
    vol30 = r.rolling(30).std()
    P = {}
    P['H1_referencia'] = sig50.shift(1)
    P['E1_canal_20_10'] = pd.DataFrame({s: canal(C_[s], V[s], TB[s], 20, 10) for s in C_}).shift(1)
    P['E2_tendencia_vol'] = (sig50 * np.minimum(1.0, 0.5 / (vol30 * math.sqrt(365)))).shift(1)
    btc = sig50['BTCUSDT']
    P['E3_filtro_btc'] = sig50.mul(btc, axis=0).shift(1)
    P['E4_canal_volumen'] = pd.DataFrame({s: canal(C_[s], V[s], TB[s], 20, 10, volumen=True) for s in C_}).shift(1)
    panico = (r < -2.5 * vol30.shift(1)) & (V > 2 * V.shift(1).rolling(20).mean())
    P['E5_rebote_panico'] = pd.DataFrame({s: mantener(panico[s].astype(float).where(vol30[s].shift(1).notna())) for s in C_})
    E6 = {}
    for s in C_:
        f = F.get(s, pd.Series(dtype=float)).reindex(C_.index)
        s3 = f.rolling(3).sum()
        hist = s3.shift(1).rolling(365, min_periods=365)
        p95, p5 = hist.quantile(0.95), hist.quantile(0.05)
        disp = pd.Series(np.where(s3 > p95, -1.0, np.where(s3 < p5, 1.0, 0.0)), index=C_.index).where(p95.notna())
        E6[s] = mantener(disp)
    P['E6_financiamiento'] = pd.DataFrame(E6)
    P['E1b_canal_55_20'] = pd.DataFrame({s: canal(C_[s], V[s], TB[s], 55, 20) for s in C_}).shift(1)
    return C_, r, P


def cartera(r, pos, costo, cols=None):
    cols = cols or list(r.columns)
    rr = r[cols]
    disp = rr.notna()
    n = disp.sum(axis=1).replace(0, np.nan)
    w = pos[cols].where(disp).div(n, axis=0).fillna(0.0)
    neto = (w * rr.fillna(0)).sum(axis=1) - w.diff().abs().sum(axis=1) * costo
    bh = rr.where(disp).mean(axis=1)
    return neto, bh


def evaluar(r, pos, costo, tramo, cols=None):
    y, x = cartera(r, pos, costo, cols)
    m = (y.index >= tramo[0]) & (y.index < tramo[1]) & x.notna()
    a, t, b = C.newey_west_alfa(y[m].to_numpy(), x[m].to_numpy())
    exp = float((pos[cols or list(r.columns)].where(r.notna()).fillna(0) != 0).mean(axis=1)[m].mean())
    return dict(C.metricas(y[m].to_numpy()), alfa_anual_pct=round(a * 365 * 100, 2), t_alfa=round(t, 3), beta=round(b, 3),
                exposicion=round(exp, 3)), C.metricas(x[m].to_numpy())


def main(carpeta, salida):
    t0 = time.time()
    D = {s: diarias(C.cargar(carpeta, 'Binance_%s_1h.csv' % s)) for s in C.MONEDAS}
    F = {s: funding_diario(carpeta, s) for s in C.MONEDAS}
    C_, r, P = posiciones(D, F)
    fin = int(C_.index.max()) + 86400
    reserva = (C.ep(2025, 6), fin)
    nombres = ['E1_canal_20_10', 'E2_tendencia_vol', 'E3_filtro_btc', 'E4_canal_volumen', 'E5_rebote_panico', 'E6_financiamiento']
    L = ['CRIPTO LOTE 2 - plan pre-registrado en docs/validacion/cripto_lote2_plan.md',
         'Cartera de igual peso, 9 monedas, costo 0,125%% por lado. Ventaja: alfa t >= %.2f en diseno; confirmada: alfa > 0 en la reserva ciega.' % T_CRITICO,
         'Financiamiento: ' + ', '.join('%s desde %s' % (s[:-4], str(dt.datetime.utcfromtimestamp(int(F[s].index.min())).date()) if len(F[s]) else '-') for s in C.MONEDAS), '']
    J = {'estrategias': {}}
    for titulo, tramo in (('DISENO 2019-01-01 a 2025-06-01', DISENO), ('RESERVA CIEGA 2025-06-01 a %s' % dt.datetime.utcfromtimestamp(fin).date(), reserva)):
        L.append('== ' + titulo)
        clave = 'diseno' if tramo == DISENO else 'reserva'
        bh = None
        for k in ['H1_referencia'] + nombres:
            res, bh = evaluar(r, P[k], C.COSTO, tramo)
            J['estrategias'].setdefault(k, {})[clave] = res
            L.append('  %-20s anual %8.2f%%  sharpe %6.3f  caida %6.2f%%  expuesto %4.0f%% | alfa %7.2f%% t %5.2f beta %5.2f' % (
                k, res['retorno_anual_pct'], res['sharpe'], res['caida_max_pct'], res['exposicion'] * 100,
                res['alfa_anual_pct'], res['t_alfa'], res['beta']))
        J.setdefault('comprar_mantener', {})[clave] = bh
        L.append('  %-20s anual %8.2f%%  sharpe %6.3f  caida %6.2f%%' % ('comprar y mantener', bh['retorno_anual_pct'], bh['sharpe'], bh['caida_max_pct']))
        L.append('')
    L.append('== VEREDICTOS')
    for k in nombres:
        d, rv, h1 = J['estrategias'][k]['diseno'], J['estrategias'][k]['reserva'], J['estrategias']['H1_referencia']['reserva']
        ventaja = d['t_alfa'] >= T_CRITICO and d['alfa_anual_pct'] > 0
        confirmada = ventaja and rv['alfa_anual_pct'] > 0
        mejora = confirmada and (rv['sharpe'] > h1['sharpe'] or rv['caida_max_pct'] < h1['caida_max_pct'])
        v = 'CONFIRMADA' + (' y mejora a H1' if mejora else ', no mejora a H1') if confirmada else ('VENTAJA EN DISENO, falla en reserva' if ventaja else 'SIN VENTAJA')
        J['estrategias'][k]['veredicto'] = v
        L.append('  %-20s %s' % (k, v))
    L += ['', '== SECUNDARIAS (informativas)']
    for c in COSTOS_SENS:
        L.append('  costo %.2f%% por lado (diseno): ' % (c * 100) + ', '.join(
            '%s t %.2f' % (k[:2], evaluar(r, P[k], c, DISENO)[0]['t_alfa']) for k in nombres))
    e1b_d, _ = evaluar(r, P['E1b_canal_55_20'], C.COSTO, DISENO)
    e1b_r, _ = evaluar(r, P['E1b_canal_55_20'], C.COSTO, reserva)
    J['estrategias']['E1b_canal_55_20'] = {'diseno': e1b_d, 'reserva': e1b_r}
    L.append('  E1b canal 55/20: diseno alfa %.2f%% t %.2f sharpe %.2f | reserva alfa %.2f%% t %.2f sharpe %.2f' % (
        e1b_d['alfa_anual_pct'], e1b_d['t_alfa'], e1b_d['sharpe'], e1b_r['alfa_anual_pct'], e1b_r['t_alfa'], e1b_r['sharpe']))
    L.append('  por moneda en diseno (t del alfa):')
    for s in C.MONEDAS:
        L.append('    %-9s ' % s + '  '.join('%s %5.2f' % (k[:2], evaluar(r, P[k], C.COSTO, DISENO, [s])[0]['t_alfa']) for k in ['H1_referencia'] + nombres))
    L += ['', 'Tiempo total: %.1f min' % ((time.time() - t0) / 60)]
    open(salida + '.txt', 'w', encoding='utf-8').write('\n'.join(L) + '\n')
    json.dump(J, open(salida + '.json', 'w', encoding='utf-8'), indent=1, default=float)
    print('\n'.join(L))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
