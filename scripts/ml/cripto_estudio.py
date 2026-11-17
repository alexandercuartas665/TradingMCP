"""Estudio pre-registrado de viabilidad en cripto (docs/validacion/cripto_plan.md).

H1 tendencia diaria (media 50), H2 momentum de serie semanal, H3 momentum cruzado semanal,
H4 LightGBM a 24h con volumen y flujo, mas secundarias (costos, subperiodos, Deriv multiplicadores).

Uso (entorno de D:):
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/cripto_estudio.py D:/traiding_ml docs/validacion/cripto_2026-11-17
"""
import datetime as dt
import json
import math
import os
import sys
import time

import lightgbm as lgb
import numpy as np
import pandas as pd

MONEDAS = ['BTCUSDT', 'ETHUSDT', 'BNBUSDT', 'LTCUSDT', 'XRPUSDT', 'ADAUSDT', 'LINKUSDT', 'DOGEUSDT', 'SOLUSDT']
COSTO = 0.00125
COSTOS_SENS = [0.0005, 0.002]
T_CRITICO = 2.24
SEMILLA = 42
COBERTURAS = {'5%': 95, '10%': 90, '20%': 80}
HZ = 24


def ep(a, m=1, d=1):
    return int(dt.datetime(a, m, d, tzinfo=dt.timezone.utc).timestamp())


INICIO_PRUEBA = ep(2019)
INICIO_ML = ep(2017, 9)
CORTE = ep(2025, 6)
SEPARACION = 86400


def cargar(carpeta, nombre):
    df = pd.read_csv(os.path.join(carpeta, 'cripto', 'datos', nombre))
    return df.drop_duplicates('epoch').sort_values('epoch').reset_index(drop=True)


# ================================================================== H1-H3
def diarias(h):
    d = h.assign(dia=h['epoch'] // 86400 * 86400).groupby('dia').agg(c=('c', 'last'), n=('c', 'size'))
    return d[d['n'] >= 20]['c']


def newey_west_alfa(y, x, rezagos=5):
    X = np.column_stack([np.ones(len(x)), x])
    b = np.linalg.lstsq(X, y, rcond=None)[0]
    e = y - X @ b
    n = len(y)
    S = (X * e[:, None]).T @ (X * e[:, None]) / n
    for l in range(1, rezagos + 1):
        w = 1 - l / (rezagos + 1)
        G = (X[l:] * e[l:, None]).T @ (X[:-l] * e[:-l, None]) / n
        S += w * (G + G.T)
    Q = np.linalg.inv(X.T @ X / n)
    V = Q @ S @ Q / n
    return float(b[0]), float(b[0] / math.sqrt(V[0, 0])), float(b[1])


def metricas(r):
    r = np.asarray(r)
    eq = np.cumprod(1 + r)
    caida = float(np.max(1 - eq / np.maximum.accumulate(eq)))
    sd = r.std(ddof=1)
    return {'dias': len(r), 'retorno_anual_pct': round(float((eq[-1] ** (365 / len(r)) - 1) * 100), 2),
            'sharpe': round(float(r.mean() / sd * math.sqrt(365)), 3) if sd > 0 else 0.0,
            'caida_max_pct': round(caida * 100, 2), 'multiplica_x': round(float(eq[-1]), 3)}


def carteras(cierres, costo):
    """cierres: DataFrame dia x moneda. Devuelve dict de series diarias netas y comprar-mantener."""
    r = cierres.pct_change()
    dias = cierres.index
    lunes = pd.Series(pd.to_datetime(dias, unit='s', utc=True).dayofweek == 0, index=dias)
    # H1: senal al cierre del dia t -> posicion el dia t+1
    sma = cierres.rolling(50).mean()
    pos1 = (cierres > sma).astype(float).where(sma.notna()).shift(1)
    # H2/H3: cada lunes con el cierre del domingo (dia anterior), se mantiene 7 dias
    r28 = cierres / cierres.shift(28) - 1
    senal_dom = r28.shift(1)
    pos2 = pd.DataFrame(np.nan, index=dias, columns=cierres.columns)
    pos3 = pd.DataFrame(np.nan, index=dias, columns=cierres.columns)
    for d in dias[lunes.to_numpy()]:
        s = senal_dom.loc[d].dropna()
        s = s[r.loc[d, s.index].notna()]
        pos2.loc[d, s.index] = np.sign(s)
        if len(s) >= 6:
            orden = s.sort_values()
            pos3.loc[d, s.index] = 0.0
            pos3.loc[d, orden.index[-3:]] = 1.0
            pos3.loc[d, orden.index[:3]] = -1.0
    pos2 = pos2.ffill(limit=6)
    pos3 = pos3.ffill(limit=6)
    disp = r.notna()
    n = disp.sum(axis=1).replace(0, np.nan)
    bh = r.where(disp).mean(axis=1)
    out = {'comprar_mantener': bh}
    for nombre, pos, escala in (('H1_tendencia', pos1, None), ('H2_momentum_serie', pos2, None), ('H3_momentum_cruzado', pos3, 3.0)):
        p = pos.where(disp)
        w = p.div(escala) if escala else p.div(n, axis=0)
        w = w.fillna(0.0)
        bruto = (w * r.fillna(0)).sum(axis=1)
        giro = w.diff().abs().sum(axis=1)
        out[nombre] = bruto - giro * costo
    return out


def prueba_reglas(series_d, costo, desde, hasta):
    cierres = pd.DataFrame(series_d)
    cierres = cierres[cierres.index < hasta]
    c = carteras(cierres, costo)
    m = (c['comprar_mantener'].index >= desde) & c['comprar_mantener'].notna()
    x = c['comprar_mantener'][m].to_numpy()
    res = {'comprar_mantener': metricas(x)}
    for k in ('H1_tendencia', 'H2_momentum_serie', 'H3_momentum_cruzado'):
        y = c[k][m].to_numpy()
        a, t, beta = newey_west_alfa(y, x)
        res[k] = dict(metricas(y), alfa_anual_pct=round(a * 365 * 100, 2), t_alfa=round(t, 3), beta=round(beta, 3))
    return res


# ================================================================== H4
def rsi(c, n=14):
    d = c.diff()
    s = d.clip(lower=0).ewm(alpha=1 / n, adjust=False).mean()
    b = (-d.clip(upper=0)).ewm(alpha=1 / n, adjust=False).mean()
    return 100 - 100 / (1 + s / b.replace(0, np.nan))


def variables(df):
    c, o, h, l = df['c'], df['o'], df['h'], df['l']
    v = df['volumen'].replace(0, np.nan)
    lr = np.log(c).diff()
    vol24 = lr.rolling(24).std().replace(0, np.nan)
    f = pd.DataFrame({'epoch': df['epoch']})
    for k in [1, 4, 12, 24, 72, 168]:
        f['r%d' % k] = np.log(c / c.shift(k)) / (vol24 * math.sqrt(k))
    f['vol24'] = vol24
    f['vol_razon'] = vol24 / lr.rolling(168).std().replace(0, np.nan)
    rango = (h - l).replace(0, np.nan)
    f['rango'] = (h - l) / c / vol24
    f['cuerpo'] = (c - o) / rango
    f['rsi14'] = rsi(c)
    f['bb20'] = (c - c.rolling(20).mean()) / c.rolling(20).std().replace(0, np.nan)
    f['dist_max168'] = (c - h.rolling(168).max()) / c / vol24
    f['dist_min168'] = (c - l.rolling(168).min()) / c / vol24
    f['vol_rel'] = v.rolling(24).sum() / (v.rolling(720).sum() / 30)
    f['compra_ag_1h'] = df['compra_agresiva'] / v
    f['compra_ag_24h'] = df['compra_agresiva'].rolling(24).sum() / v.rolling(24).sum()
    f['oper_rel'] = df['operaciones'].rolling(24).sum() / (df['operaciones'].rolling(720).sum() / 30)
    cierre = df['epoch'] + 3600
    hora = (cierre % 86400) / 3600.0
    f['hora_sin'] = np.sin(2 * np.pi * hora / 24)
    f['hora_cos'] = np.cos(2 * np.pi * hora / 24)
    f['dia'] = pd.to_datetime(cierre, unit='s', utc=True).dt.day_of_week
    fut = c.shift(-HZ)
    ok = (df['epoch'].shift(-HZ) - df['epoch'] == HZ * 3600) & fut.notna() & (fut != c)
    f['ret'] = (fut / c - 1).where(ok)
    f['y'] = (fut > c).astype(float).where(ok)
    f['_r4'] = f['r4']
    f['_r24'] = f['r24']
    return f


def construir(horas):
    partes = {}
    for sym, df in horas.items():
        f = variables(df)
        f['symbol'] = sym
        partes[sym] = f
    cruz = None
    for sym in ('BTCUSDT', 'ETHUSDT'):
        x = partes[sym][['epoch', '_r4', '_r24']].rename(columns={'_r4': 'x_%s_r4' % sym[:3], '_r24': 'x_%s_r24' % sym[:3]})
        cruz = x if cruz is None else cruz.merge(x, on='epoch', how='outer')
    todo = []
    for sym, f in partes.items():
        g = f.drop(columns=['_r4', '_r24']).merge(cruz, on='epoch', how='left')
        for s in MONEDAS:
            g['m_' + s[:-4]] = float(s == sym)
        todo.append(g)
    d = pd.concat(todo, ignore_index=True)
    d['cierre'] = d['epoch'] + 3600
    d = d[(d['cierre'] % 14400) == 0].reset_index(drop=True)          # decisiones cada 4 horas
    return d


def columnas(d):
    return [k for k in d.columns if k not in ('epoch', 'cierre', 'symbol', 'ret', 'y')]


def entrenar(d, cols, hasta):
    tr = d[(d['cierre'] >= INICIO_ML) & (d['cierre'] + HZ * 3600 < hasta - SEPARACION) & d['y'].notna()]
    m = lgb.LGBMClassifier(n_estimators=300, learning_rate=0.03, num_leaves=31, min_child_samples=500, subsample=0.8,
                           subsample_freq=1, colsample_bytree=0.8, n_jobs=8, random_state=SEMILLA, verbose=-1)
    m.fit(tr[cols].to_numpy(np.float32), tr['y'].to_numpy())
    p = m.predict_proba(tr[cols].to_numpy(np.float32))[:, 1]
    umb = {k: float(np.percentile(np.abs(p - 0.5), q)) for k, q in COBERTURAS.items()}
    return m, umb, len(tr)


def operar(filas, p, umbral, solo_largos=False):
    sel = filas.assign(p=p)
    sel = sel[np.abs(sel['p'] - 0.5) >= umbral]
    if solo_largos:
        sel = sel[sel['p'] > 0.5]
    sel = sel.sort_values(['symbol', 'cierre'])
    tomadas = []
    for sym, g in sel.groupby('symbol'):
        libre = -1
        for cierre, pp, ret in zip(g['cierre'].to_numpy(), g['p'].to_numpy(), g['ret'].to_numpy()):
            if cierre < libre:
                continue
            tomadas.append((sym, int(cierre), 1.0 if pp > 0.5 else -1.0, float(ret)))
            libre = cierre + HZ * 3600
    return tomadas


def resumen(ops, costo=COSTO):
    n = len(ops)
    if n < 2:
        return {'operaciones': n}
    bruto = np.array([o[2] * o[3] for o in ops])
    neto = bruto - 2 * costo
    se = neto.std(ddof=1) / math.sqrt(n)
    return {'operaciones': n, 'acierto': round(float((bruto > 0).mean()), 4),
            'bruto_medio_pct': round(float(bruto.mean() * 100), 4), 'neto_medio_pct': round(float(neto.mean() * 100), 4),
            'z_neto': round(float(neto.mean() / se), 3) if se > 0 else 0.0,
            'neto_total_pct': round(float(neto.sum() * 100), 2)}


def deriv_multiplicadores(carpeta, ops):
    """Senales de BTC/ETH en reserva 2 sobre velas de Deriv: x100, stop out con max/min de 1h, cierre 24h."""
    com = {'BTCUSDT': 0.00032, 'ETHUSDT': 0.00085}
    res = {}
    for sym, dsym in (('BTCUSDT', 'cryBTCUSD'), ('ETHUSDT', 'cryETHUSD')):
        dv = cargar(carpeta, 'Deriv_%s_1h.csv' % dsym)
        dv = dv[dv['epoch'] % 3600 == 0].set_index('epoch')
        mult = 100
        lim = (1 - com[sym] * mult) / mult
        pnl = []
        liquidadas = 0
        for s, cierre, d, _ in ops:
            if s != sym or (cierre - 3600) not in dv.index:
                continue
            entrada = dv.loc[cierre - 3600, 'c']
            fut = dv.loc[cierre:cierre + (HZ - 1) * 3600]
            if len(fut) < HZ:
                continue
            peor = (fut['l'].min() / entrada - 1) if d > 0 else (1 - fut['h'].max() / entrada)
            if peor <= -lim:
                pnl.append(-1.0)
                liquidadas += 1
                continue
            mov = d * (fut['c'].iloc[-1] / entrada - 1)
            pnl.append(max(-1.0, mult * mov - com[sym] * mult))
        if pnl:
            a = np.array(pnl)
            res[dsym] = {'operaciones': len(a), 'liquidadas': liquidadas, 'pnl_medio_por_dolar': round(float(a.mean()), 4),
                         'pnl_total_por_dolar': round(float(a.sum()), 2)}
        else:
            res[dsym] = {'operaciones': 0}
    return res


def control_fuente(carpeta, horas):
    out = {}
    for sym, dsym in (('BTCUSDT', 'cryBTCUSD'), ('ETHUSDT', 'cryETHUSD')):
        dv = cargar(carpeta, 'Deriv_%s_1h.csv' % dsym)
        dv = dv[dv['epoch'] % 3600 == 0][['epoch', 'c']]
        b = horas[sym][['epoch', 'c']]
        m = dv.merge(b, on='epoch', suffixes=('_d', '_b')).sort_values('epoch')
        rd, rb = m['c_d'].diff(), m['c_b'].diff()
        ok = (rd != 0) & (rb != 0) & rd.notna()
        out[dsym] = {'velas': int(ok.sum()), 'misma_direccion': round(float((np.sign(rd[ok]) == np.sign(rb[ok])).mean()), 4),
                     'diferencia_media_pct': round(float((m['c_d'] / m['c_b'] - 1).abs().mean() * 100), 4)}
    return out


# ================================================================== main
def main(carpeta, salida):
    t0 = time.time()
    horas = {s: cargar(carpeta, 'Binance_%s_1h.csv' % s) for s in MONEDAS}
    fin = max(int(h['epoch'].max()) for h in horas.values()) + 3600
    L = ['ESTUDIO DE VIABILIDAD EN CRIPTO - plan pre-registrado en docs/validacion/cripto_plan.md',
         'Binance spot 1h, %d monedas. Costo principal %.3f%% por lado. Ventaja: t/z >= %.2f.' % (len(MONEDAS), COSTO * 100, T_CRITICO), '']
    J = {'cobertura': {s: [str(dt.datetime.utcfromtimestamp(int(h['epoch'].min())).date()), len(h)] for s, h in horas.items()}}

    # ---- H1-H3
    series_d = {s: diarias(h) for s, h in horas.items()}
    L.append('== H1-H3 reglas clasicas, cartera de igual peso, 2019-01-01 a %s' % dt.datetime.utcfromtimestamp(fin).date())
    J['reglas'] = {}
    for etiqueta, costo, desde, hasta in [('principal', COSTO, INICIO_PRUEBA, fin)] + \
            [('costo %.2f%%' % (c * 100), c, INICIO_PRUEBA, fin) for c in COSTOS_SENS] + \
            [('2019-2021', COSTO, INICIO_PRUEBA, ep(2022)), ('2022-2026', COSTO, ep(2022), fin)]:
        r = prueba_reglas(series_d, costo, desde, hasta)
        J['reglas'][etiqueta] = r
        L.append('  [%s]' % etiqueta)
        bh = r['comprar_mantener']
        L.append('    %-22s anual %8.2f%%  sharpe %6.3f  caida max %6.2f%%  x%.2f' % (
            'comprar y mantener', bh['retorno_anual_pct'], bh['sharpe'], bh['caida_max_pct'], bh['multiplica_x']))
        for k in ('H1_tendencia', 'H2_momentum_serie', 'H3_momentum_cruzado'):
            x = r[k]
            ventaja = etiqueta == 'principal' and x['t_alfa'] >= T_CRITICO and x['alfa_anual_pct'] > 0
            L.append('    %-22s anual %8.2f%%  sharpe %6.3f  caida max %6.2f%%  x%.2f | alfa anual %7.2f%% t %5.2f beta %5.2f%s' % (
                k, x['retorno_anual_pct'], x['sharpe'], x['caida_max_pct'], x['multiplica_x'], x['alfa_anual_pct'], x['t_alfa'],
                x['beta'], ('  => VENTAJA' if ventaja else '  => sin ventaja') if etiqueta == 'principal' else ''))
            if etiqueta == 'principal':
                J['reglas']['veredicto_' + k] = 'VENTAJA' if ventaja else 'SIN VENTAJA'
    print('\n'.join(L), flush=True)

    # ---- H4
    d = construir(horas)
    cols = columnas(d)
    print('H4: %d filas, %d variables (%.0fs)' % (len(d), len(cols), time.time() - t0), flush=True)
    L += ['', '== H4 LightGBM, prediccion a 24h cada 4h, %d variables, costo 0,25%% ida y vuelta' % len(cols)]
    base = d[d['y'].notna()]
    r1 = {k: [] for k in COBERTURAS}
    por_anio = []
    tramos = [(ep(a), ep(a + 1)) for a in range(2021, 2025)] + [(ep(2025), CORTE)]
    for ini, fn in tramos:
        m, umb, ntr = entrenar(d, cols, ini)
        f = base[(base['cierre'] >= ini) & (base['cierre'] < fn)]
        p = m.predict_proba(f[cols].to_numpy(np.float32))[:, 1]
        filas = f[['symbol', 'cierre', 'ret']]
        for k in COBERTURAS:
            r1[k] += operar(filas, p, umb[k])
        rr = resumen(operar(filas, p, umb['10%']))
        por_anio.append(dict(desde=str(dt.datetime.utcfromtimestamp(ini).date()), entrenamiento=ntr, **rr))
        print('  tramo %s: %s' % (dt.datetime.utcfromtimestamp(ini).date(), rr), flush=True)
    m, umb, ntr = entrenar(d, cols, CORTE)
    m.booster_.save_model(os.path.join(carpeta, 'modelos', 'cripto_lightgbm_24h.txt'))
    f2 = base[base['cierre'] >= CORTE]
    p2 = m.predict_proba(f2[cols].to_numpy(np.float32))[:, 1]
    filas2 = f2[['symbol', 'cierre', 'ret']]
    r2 = {k: operar(filas2, p2, umb[k]) for k in COBERTURAS}
    R1 = {k: resumen(v) for k, v in r1.items()}
    R2 = {k: resumen(v) for k, v in r2.items()}
    a, b = R1['10%'], R2['10%']
    ventaja4 = a.get('operaciones', 0) >= 30 and a['z_neto'] >= T_CRITICO and a['neto_medio_pct'] > 0 and \
        b.get('operaciones', 0) > 0 and b['neto_medio_pct'] > 0
    J['H4'] = {'variables': cols, 'reserva1': R1, 'reserva2': R2, 'por_tramo': por_anio, 'umbrales': umb,
               'veredicto': 'VENTAJA' if ventaja4 else 'SIN VENTAJA'}
    L.append('  H4 => %s' % J['H4']['veredicto'])
    for titulo, R in (('reserva 1 2021-2025/05', R1), ('reserva 2 ciega 2025/06+', R2)):
        for k in COBERTURAS:
            x = R[k]
            if x.get('operaciones', 0) < 2:
                L.append('    %-26s %3s: sin operaciones' % (titulo, k))
                continue
            L.append('    %-26s %3s: %5d ops  acierto %6.2f%%  bruto %+.3f%%  neto %+.3f%%  z neto %5.2f  neto total %+8.1f%%' % (
                titulo, k, x['operaciones'], x['acierto'] * 100, x['bruto_medio_pct'], x['neto_medio_pct'], x['z_neto'], x['neto_total_pct']))
    L.append('    por tramo (10%): ' + ', '.join('%s %s%% neto %s%%' % (t['desde'][:7], round(t['acierto'] * 100, 1) if t.get('acierto') else '-',
                                                                       t.get('neto_medio_pct', '-')) for t in por_anio))
    # secundarias H4
    sec = {}
    for c in COSTOS_SENS:
        sec['reserva2_10_costo_%.2f' % (c * 100)] = resumen(r2['10%'], c)
    sec['reserva2_10_solo_largos'] = resumen(operar(filas2, p2, umb['10%'], solo_largos=True))
    sec['reserva2_10_por_moneda'] = {s: resumen([o for o in r2['10%'] if o[0] == s]) for s in MONEDAS}
    sec['reserva1_10_por_moneda'] = {s: resumen([o for o in r1['10%'] if o[0] == s]) for s in MONEDAS}
    sec['deriv_multiplicadores_x100'] = deriv_multiplicadores(carpeta, r2['10%'])
    sec['control_fuente_deriv_binance'] = control_fuente(carpeta, horas)
    J['secundarias'] = sec
    L.append('  secundarias (informativas):')
    for c in COSTOS_SENS:
        x = sec['reserva2_10_costo_%.2f' % (c * 100)]
        L.append('    reserva 2 10%% con costo %.2f%% por lado: neto %+.3f%% z %.2f' % (c * 100, x.get('neto_medio_pct', 0), x.get('z_neto', 0)))
    x = sec['reserva2_10_solo_largos']
    L.append('    reserva 2 10%% solo largos: %s ops acierto %s neto %+.3f%%' % (x.get('operaciones'), x.get('acierto'), x.get('neto_medio_pct', 0)))
    for s in MONEDAS:
        x1, x2 = sec['reserva1_10_por_moneda'][s], sec['reserva2_10_por_moneda'][s]
        L.append('    %-9s reserva1 %4s ops neto %+.3f%% | reserva2 %4s ops neto %+.3f%%' % (
            s, x1.get('operaciones'), x1.get('neto_medio_pct', 0), x2.get('operaciones'), x2.get('neto_medio_pct', 0)))
    for k, x in sec['deriv_multiplicadores_x100'].items():
        L.append('    Deriv multiplicador x100 %s: %s' % (k, x))
    for k, x in sec['control_fuente_deriv_binance'].items():
        L.append('    control fuente %s: %s' % (k, x))
    L += ['', 'Tiempo total: %.1f min' % ((time.time() - t0) / 60)]
    open(salida + '.txt', 'w', encoding='utf-8').write('\n'.join(L) + '\n')
    json.dump(J, open(salida + '.json', 'w', encoding='utf-8'), indent=1, default=float)
    print('\n'.join(L))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
