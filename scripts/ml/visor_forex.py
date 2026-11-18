"""Genera el visor HTML de como decide el modelo LightGBM de binarias (forex y metales, Rise/Fall 2h).

Mismo modelo y umbrales del forward test (docs/validacion/forward_ml_plan.md): lightgbm_binarias.txt,
entrenado con Dukascopy hasta 2024-04-01. Se muestra el ultimo ano de velas de 1h de Deriv (datos que el
modelo nunca vio). Reglas: nivel 5%, una posicion a la vez por mercado, 2 horas, payout 72,45%, y las
horas que Deriv no deja comprar (entrada 20:00-23:59 UTC: el vencimiento cae en el cierre 21:50-23:00 o
el contrato de menos de 5h no se ofrece de 21:00 a 23:59).

Uso (entorno de D:):
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/visor_forex.py D:/traiding_ml <salida.html>
La plantilla es scripts/ml/visor_forex_plantilla.html (el JSON reemplaza /*__DATOS__*/null).
"""
import datetime as dt
import json
import math
import os
import sys

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)

import lightgbm as lgb  # noqa: E402
import numpy as np  # noqa: E402

import modelo_ml as mm  # noqa: E402
from visor_agente import r4, r6  # noqa: E402

DESDE = int(dt.datetime(2025, 11, 1, tzinfo=dt.timezone.utc).timestamp())
HZ = mm.HORIZONTES['binarias']
PAYOUT = 0.7245
NIVEL = '5%'
HORAS_BLOQUEADAS = {20, 21, 22, 23}
FAMILIAS = [
    ('Impulso del precio', ['r1', 'r2', 'r3', 'r4', 'r6', 'r12', 'r24', 'r120', 'ema_dif']),
    ('Posicion en su rango', ['rsi14', 'bb20', 'cci20', 'dist_max24', 'dist_min24']),
    ('Volatilidad y forma de vela', ['vol24', 'vol120', 'vol_razon', 'rango', 'cuerpo', 'mecha_sup', 'mecha_inf']),
    ('Hora y dia', ['hora_sin', 'hora_cos', 'dia']),
    ('Noticias de EE. UU.', ['noticia_prox2h', 'noticia_ult2h']),
    ('Otros mercados', ['x_%s_%s' % (m[3:], s) for m in mm.MERCADOS for s in ('r1', 'r6')]),
    ('Que mercado es', ['m_' + m[3:] for m in mm.MERCADOS]),
]
NOMBRES = {
    'r1': 'Retorno 1h', 'r2': 'Retorno 2h', 'r3': 'Retorno 3h', 'r4': 'Retorno 4h', 'r6': 'Retorno 6h', 'r12': 'Retorno 12h',
    'r24': 'Retorno 24h', 'r120': 'Retorno 5 dias', 'ema_dif': 'MACD (EMA 12 - EMA 26)', 'vol24': 'Volatilidad 24h',
    'vol120': 'Volatilidad 5 dias', 'vol_razon': 'Volatilidad 24h / 5 dias', 'rango': 'Rango de la vela', 'cuerpo': 'Cuerpo de la vela',
    'mecha_sup': 'Mecha superior', 'mecha_inf': 'Mecha inferior', 'rsi14': 'RSI 14', 'bb20': 'Bollinger 20', 'cci20': 'CCI 20',
    'dist_max24': 'Distancia al maximo de 24h', 'dist_min24': 'Distancia al minimo de 24h', 'hora_sin': 'Hora del dia',
    'hora_cos': 'Hora del dia', 'dia': 'Dia de la semana', 'noticia_prox2h': 'Noticia en las proximas 2h',
    'noticia_ult2h': 'Noticia en las ultimas 2h',
}
for m in mm.MERCADOS:
    NOMBRES['x_%s_r1' % m[3:]] = '%s ultima hora' % m[3:]
    NOMBRES['x_%s_r6' % m[3:]] = '%s ultimas 6h' % m[3:]
UNIDAD = {k: 'sigma' for k in ('r1', 'r2', 'r3', 'r4', 'r6', 'r12', 'r24', 'r120', 'ema_dif', 'bb20', 'dist_max24', 'dist_min24', 'rango')}
UNIDAD.update({k: 'sigma' for k in NOMBRES if k.startswith('x_')})
UNIDAD.update({'vol24': 'pct', 'vol120': 'pct', 'vol_razon': 'x', 'cuerpo': 'frac', 'mecha_sup': 'frac01', 'mecha_inf': 'frac01',
               'rsi14': 'num', 'cci20': 'num', 'hora_sin': 'hora', 'hora_cos': 'hora', 'dia': 'dia',
               'noticia_prox2h': 'si', 'noticia_ult2h': 'si'})


def r3(v):
    return float('%.3g' % v) if v == v else None


def main(carpeta, salida):
    raiz = os.path.join(AQUI, '..', '..', 'docs', 'validacion')
    ev = json.load(open(os.path.join(raiz, 'eventos_eeuu_2003_2024.json'), encoding='utf-8'))['eventos']
    eventos = [(int(dt.datetime.fromisoformat(e['inicio_utc'].replace('Z', '+00:00')).timestamp()),
                int(dt.datetime.fromisoformat(e['fin_utc'].replace('Z', '+00:00')).timestamp())) for e in ev]
    J = json.load(open(os.path.join(raiz, 'modelo_ml_2026-11-16.json'), encoding='utf-8'))
    cols = J['variables']
    umbral = float(J['resultados']['binarias_lightgbm']['umbrales_final'][NIVEL])
    series = {s: mm.cargar(carpeta, 'Deriv', s) for s in mm.MERCADOS}
    d = mm.construir_desde_series(series, eventos)
    d = d[d['cierre'] >= mm.FIN_DUKA].reset_index(drop=True)
    b = lgb.Booster(model_file=os.path.join(carpeta, 'modelos', 'lightgbm_binarias.txt'))
    idx = {k: i for i, k in enumerate(cols)}
    fam_ix = [[idx[k] for k in ks] for _, ks in FAMILIAS]
    expl = [k for k in cols if not k.startswith('m_')]
    X = d[cols].to_numpy(np.float32)
    P = b.predict(X)
    CON = b.predict(X, pred_contrib=True)
    base = float(CON[0, -1])
    ia = {}
    for sym in mm.MERCADOS:
        sel = np.where((d['symbol'] == sym).to_numpy() & (d['cierre'] >= DESDE).to_numpy())[0]
        sel = sel[np.argsort(d['cierre'].to_numpy()[sel])]
        libre, dec = -1, []
        for i in sel:
            cierre = int(d['cierre'].iloc[i])
            ret = d['ret_binarias'].iloc[i]
            p = float(P[i])
            senal = abs(p - 0.5) >= umbral
            hora = (cierre % 86400) // 3600
            if senal and hora in HORAS_BLOQUEADAS:
                estado = 3
            elif senal and cierre >= libre and ret == ret:
                estado = 1 if p > 0.5 else -1
                libre = cierre + HZ * 3600
            elif senal:
                estado = 2
            else:
                estado = 0
            fam = [round(float(CON[i, ix].sum()), 2) for ix in fam_ix]
            tops = sorted(expl, key=lambda k: -abs(CON[i, idx[k]]))[:5]
            top = [[idx[k], r3(float(X[i, idx[k]])), round(float(CON[i, idx[k]]), 2)] for k in tops]
            dec.append([cierre, round(p, 3), estado, r3(float(ret)) if ret == ret else None, fam, top])
        v = series[sym]
        v = v[(v['epoch'] >= DESDE - 3600)]
        ia[sym[3:]] = {'velas': [[int(e), r6(o), r6(h), r6(l), r6(c)] for e, o, h, l, c in v[['epoch', 'o', 'h', 'l', 'c']].itertuples(index=False)],
                       'dec': dec}
    # lo que aprendio: efecto medio por valor, todo el periodo Deriv (2024-04 en adelante)
    dep, imp = {}, {}
    hora = ((d['cierre'] % 86400) // 3600).to_numpy()
    for k in expl:
        if k == 'hora_cos':
            continue
        if k == 'hora_sin':
            c = CON[:, idx['hora_sin']] + CON[:, idx['hora_cos']]
            dep['hora'] = [[int(hh), round(float(c[hora == hh].mean()), 4), int((hora == hh).sum())] for hh in sorted(set(hora))]
            imp['hora'] = round(float(np.abs(c).mean()), 4)
            continue
        v = d[k].to_numpy(dtype=float)
        c = CON[:, idx[k]]
        ok = ~np.isnan(v)
        imp[k] = round(float(np.abs(c).mean()), 4)
        valores = np.unique(v[ok])
        if len(valores) <= 7:
            dep[k] = [[r4(float(x)), round(float(c[ok & (v == x)].mean()), 4), int((ok & (v == x)).sum())] for x in valores]
            continue
        cortes = np.unique(np.nanpercentile(v[ok], np.linspace(2, 98, 19)))
        cajas = np.digitize(v[ok], cortes)
        dep[k] = [[r4(float(v[ok][cajas == j].mean())), round(float(c[ok][cajas == j].mean()), 4), int((cajas == j).sum())]
                  for j in range(1, len(cortes)) if (cajas == j).sum() > 50]
    meta = {'claves': cols, 'umbral': umbral, 'nivel': NIVEL, 'base_logodds': base, 'base_prob': 1 / (1 + math.exp(-base)),
            'familias': [n for n, _ in FAMILIAS], 'nombres': NOMBRES, 'unidad': UNIDAD, 'payout': PAYOUT,
            'horizonte_h': HZ, 'vela_s': 3600, 'desde': DESDE, 'n_variables': len(cols), 'horas_bloqueadas': sorted(HORAS_BLOQUEADAS),
            'dependencia': dep, 'importancia': imp,
            'importancia_familia': [round(float(np.abs(CON[:, ix].sum(axis=1)).mean()), 4) for ix in fam_ix]}
    datos = json.dumps({'meta': meta, 'ia': ia}, separators=(',', ':'))
    plantilla = open(os.path.join(AQUI, 'visor_forex_plantilla.html'), encoding='utf-8').read()
    html = plantilla.replace('/*__DATOS__*/null', datos)
    open(salida, 'w', encoding='utf-8').write(html)
    print('visor: %s (%.1f MB)' % (salida, len(html) / 1e6))
    for s, x in ia.items():
        ops = [y for y in x['dec'] if y[2] in (1, -1)]
        bloq = sum(1 for y in x['dec'] if y[2] == 3)
        gan = [(y[2] * y[3]) > 0 for y in ops]
        usd = sum(PAYOUT if g else -1.0 for g in gan)
        print('  %s: %d velas, %d operaciones, acierto %.1f%%, %+.1f USD por 1 USD, %d senales en horas bloqueadas' % (
            s, len(x['velas']), len(ops), (np.mean(gan) * 100) if gan else 0, usd, bloq))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
