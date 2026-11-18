"""Genera el visor HTML de como decide el agente (modelo LightGBM 24h de cripto y regla E2).

Para cada decision del modelo (cada 4h, periodo que el modelo nunca vio: 2025-06-01 en adelante)
guarda la probabilidad, la decision (con las mismas reglas de H4: umbral del 10%, una posicion a la
vez por moneda, 24h) y cuanto empujo cada variable (contribuciones SHAP nativas de LightGBM, en
log-odds). Para E2 guarda por dia el cierre, la media de 50, la volatilidad de 30 dias y el peso.

Uso (entorno de D:):
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/visor_agente.py D:/traiding_ml <salida.html>
La plantilla es scripts/ml/visor_agente_plantilla.html (el JSON reemplaza /*__DATOS__*/null).
"""
import json
import math
import os
import sys

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)

import lightgbm as lgb  # noqa: E402
import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402

import cripto_estudio as C  # noqa: E402
import cripto_lote2 as L  # noqa: E402

MONEDAS_VISOR = ['BTCUSDT', 'ETHUSDT', 'SOLUSDT', 'DOGEUSDT']
DESDE_IA = C.CORTE
DESDE_E2 = C.ep(2019)
FAMILIAS = [
    ('Impulso del precio', ['r1', 'r4', 'r12', 'r24', 'r72', 'r168']),
    ('Posicion en su rango', ['rsi14', 'bb20', 'dist_max168', 'dist_min168']),
    ('Volatilidad y forma de vela', ['vol24', 'vol_razon', 'rango', 'cuerpo']),
    ('Volumen y flujo', ['vol_rel', 'compra_ag_1h', 'compra_ag_24h', 'oper_rel']),
    ('Hora y dia', ['hora_sin', 'hora_cos', 'dia']),
    ('BTC y ETH', ['x_BTC_r4', 'x_BTC_r24', 'x_ETH_r4', 'x_ETH_r24']),
    ('Que moneda es', ['m_BTC', 'm_ETH', 'm_BNB', 'm_LTC', 'm_XRP', 'm_ADA', 'm_LINK', 'm_DOGE', 'm_SOL']),
]
NOMBRES = {
    'r1': 'Retorno 1h', 'r4': 'Retorno 4h', 'r12': 'Retorno 12h', 'r24': 'Retorno 24h', 'r72': 'Retorno 3 dias',
    'r168': 'Retorno 7 dias', 'vol24': 'Volatilidad 24h', 'vol_razon': 'Volatilidad 24h / 7 dias',
    'rango': 'Rango de la vela', 'cuerpo': 'Cuerpo de la vela', 'rsi14': 'RSI 14', 'bb20': 'Bollinger 20',
    'dist_max168': 'Distancia al maximo de 7 dias', 'dist_min168': 'Distancia al minimo de 7 dias',
    'vol_rel': 'Volumen 24h vs su media', 'compra_ag_1h': 'Compra agresiva 1h', 'compra_ag_24h': 'Compra agresiva 24h',
    'oper_rel': 'Operaciones 24h vs su media', 'hora_sin': 'Hora del dia', 'hora_cos': 'Hora del dia', 'dia': 'Dia de la semana',
    'x_BTC_r4': 'BTC ultimas 4h', 'x_BTC_r24': 'BTC ultimas 24h', 'x_ETH_r4': 'ETH ultimas 4h', 'x_ETH_r24': 'ETH ultimas 24h',
}
UNIDAD = {k: 'sigma' for k in ('r1', 'r4', 'r12', 'r24', 'r72', 'r168', 'dist_max168', 'dist_min168', 'x_BTC_r4', 'x_BTC_r24', 'x_ETH_r4', 'x_ETH_r24', 'bb20', 'rango')}
UNIDAD.update({'vol24': 'pct', 'vol_razon': 'x', 'cuerpo': 'frac', 'rsi14': 'num', 'vol_rel': 'x', 'compra_ag_1h': 'pct01',
               'compra_ag_24h': 'pct01', 'oper_rel': 'x', 'hora_sin': 'hora', 'hora_cos': 'hora', 'dia': 'dia'})


def velas_4h(h, desde):
    x = h[h['epoch'] >= desde - 14400].copy()
    x['b'] = x['epoch'] // 14400 * 14400
    g = x.groupby('b').agg(o=('o', 'first'), h=('h', 'max'), l=('l', 'min'), c=('c', 'last'))
    return [[int(t), r6(r.o), r6(r.h), r6(r.l), r6(r.c)] for t, r in g.iterrows()]


def r6(v):
    return float('%.6g' % v)


def r4(v):
    return float('%.4g' % v) if v == v else None


def datos_ia(carpeta, horas):
    d = C.construir(horas)
    cols = C.columnas(d)
    J = json.load(open(os.path.join(AQUI, '..', '..', 'docs', 'validacion', 'cripto_2026-11-17.json')))
    umbral = float(J['H4']['umbrales']['10%'])
    b = lgb.Booster(model_file=os.path.join(carpeta, 'modelos', 'cripto_lightgbm_24h.txt'))
    idx = {k: i for i, k in enumerate(cols)}
    fam_ix = [[idx[k] for k in ks] for _, ks in FAMILIAS]
    expl = [k for k in cols if not k.startswith('m_')]
    out, base = {}, None
    for sym in MONEDAS_VISOR:
        f = d[(d['symbol'] == sym) & (d['cierre'] >= DESDE_IA)].sort_values('cierre').reset_index(drop=True)
        X = f[cols].to_numpy(np.float32)
        p = b.predict(X)
        con = b.predict(X, pred_contrib=True)
        base = float(con[0, -1])
        libre, dec = -1, []
        for i in range(len(f)):
            cierre = int(f['cierre'].iloc[i])
            ret = f['ret'].iloc[i]
            senal = abs(p[i] - 0.5) >= umbral
            if senal and cierre >= libre and ret == ret:
                estado = 1 if p[i] > 0.5 else -1
                libre = cierre + C.HZ * 3600
            elif senal:
                estado = 2
            else:
                estado = 0
            fam = [round(float(con[i, ix].sum()), 4) for ix in fam_ix]
            tops = sorted(expl, key=lambda k: -abs(con[i, idx[k]]))[:6]
            top = [[k, r4(float(X[i, idx[k]])), round(float(con[i, idx[k]]), 4)] for k in tops]
            dec.append([cierre, round(float(p[i]), 4), estado, r4(float(ret)) if ret == ret else None, fam, top])
        out[sym[:-4]] = {'velas': velas_4h(horas[sym], DESDE_IA), 'dec': dec}
    # lo que aprendio: efecto medio de cada variable segun su valor (todas las monedas, 2023 en adelante)
    g = d[d['cierre'] >= C.ep(2023)].reset_index(drop=True)
    cg = b.predict(g[cols].to_numpy(np.float32), pred_contrib=True)
    dep, imp = {}, {}
    hora = ((g['cierre'] % 86400) // 3600).to_numpy()
    for k in expl:
        if k == 'hora_cos':
            continue
        if k == 'hora_sin':
            c = cg[:, idx['hora_sin']] + cg[:, idx['hora_cos']]
            dep['hora'] = [[int(hh), round(float(c[hora == hh].mean()), 4), int((hora == hh).sum())] for hh in sorted(set(hora))]
            imp['hora'] = round(float(np.abs(c).mean()), 4)
            continue
        v = g[k].to_numpy()
        c = cg[:, idx[k]]
        ok = ~np.isnan(v)
        imp[k] = round(float(np.abs(c).mean()), 4)
        if k == 'dia':
            dep[k] = [[int(x), round(float(c[ok & (v == x)].mean()), 4), int((ok & (v == x)).sum())] for x in range(7)]
            continue
        cortes = np.unique(np.nanpercentile(v[ok], np.linspace(2, 98, 19)))
        cajas = np.digitize(v[ok], cortes)
        dep[k] = [[r4(float(v[ok][cajas == j].mean())), round(float(c[ok][cajas == j].mean()), 4), int((cajas == j).sum())]
                  for j in range(1, len(cortes)) if (cajas == j).sum() > 50]
    imp_fam = [round(float(np.abs(cg[:, ix].sum(axis=1)).mean()), 4) for ix in fam_ix]
    meta = {'umbral': umbral, 'base_logodds': base, 'base_prob': 1 / (1 + math.exp(-base)),
            'familias': [n for n, _ in FAMILIAS], 'nombres': NOMBRES, 'unidad': UNIDAD, 'costo_ida_vuelta': 2 * C.COSTO,
            'desde': DESDE_IA, 'horizonte_h': C.HZ, 'dependencia': dep, 'importancia': imp, 'importancia_familia': imp_fam}
    return meta, out


def datos_e2(carpeta, horas):
    D = {s: L.diarias(h) for s, h in horas.items()}
    C_, r, P = L.posiciones(D, {})
    sma = C_.rolling(50).mean()
    vol = r.rolling(30).std() * math.sqrt(365)
    sig = (C_ > sma).astype(float).where(sma.notna())
    n = C_.notna().sum(axis=1)
    peso = (sig * np.minimum(1.0, 0.5 / vol)).div(n, axis=0)
    out = {}
    for sym in MONEDAS_VISOR:
        h = horas[sym]
        x = h[h['epoch'] >= DESDE_E2].assign(dia=lambda z: z['epoch'] // 86400 * 86400)
        g = x.groupby('dia').agg(o=('o', 'first'), h=('h', 'max'), l=('l', 'min'), c=('c', 'last'), n=('c', 'size'))
        g = g[g['n'] >= 20]
        filas = []
        for t, v in g.iterrows():
            if t not in C_.index or sma.loc[t, sym] != sma.loc[t, sym]:
                continue
            filas.append([int(t), r6(v.o), r6(v.h), r6(v.l), r6(v.c), r4(float(sma.loc[t, sym])),
                          round(float(vol.loc[t, sym]), 4), int(sig.loc[t, sym]), round(float(peso.loc[t, sym]), 5), int(n.loc[t])])
        out[sym[:-4]] = filas
    eq = {}
    for k, kk in (('E2', 'E2_tendencia_vol'), ('H1', 'H1_referencia')):
        y, bh = L.cartera(r, P[kk], C.COSTO)
        eq[k] = y
        eq['BH'] = bh
    dias = [t for t in C_.index if t >= DESDE_E2]
    acum = {k: 1.0 for k in eq}
    cartera = []
    inv = P['E2_tendencia_vol'].where(r.notna()).div(r.notna().sum(axis=1), axis=0).sum(axis=1)
    for t in dias:
        for k in eq:
            v = eq[k].get(t, 0.0)
            acum[k] *= 1 + (v if v == v else 0.0)
        cartera.append([int(t), round(acum['E2'], 4), round(acum['H1'], 4), round(acum['BH'], 4), round(float(inv.get(t, 0.0)), 4)])
    return {'monedas': out, 'cartera': cartera}


def main(carpeta, salida):
    horas = {s: C.cargar(carpeta, 'Binance_%s_1h.csv' % s) for s in C.MONEDAS}
    meta, ia = datos_ia(carpeta, horas)
    e2 = datos_e2(carpeta, horas)
    datos = json.dumps({'meta': meta, 'ia': ia, 'e2': e2}, separators=(',', ':'))
    plantilla = open(os.path.join(AQUI, 'visor_agente_plantilla.html'), encoding='utf-8').read()
    html = plantilla.replace('/*__DATOS__*/null', datos)
    open(salida, 'w', encoding='utf-8').write(html)
    print('visor: %s (%.1f MB)' % (salida, len(html) / 1e6))
    for s, v in ia.items():
        ops = [x for x in v['dec'] if x[2] in (1, -1)]
        ac = np.mean([(x[2] * x[3]) > 0 for x in ops]) if ops else 0
        print('  %s: %d velas 4h, %d decisiones, %d operaciones, acierto %.1f%%' % (s, len(v['velas']), len(v['dec']), len(ops), ac * 100))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
