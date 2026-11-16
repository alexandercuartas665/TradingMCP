"""Prueba pre-registrada de aprendizaje automatico (docs/validacion/modelo_ml_plan.md).

Entrena regresion logistica y LightGBM sobre velas de 1h de 7 mercados (Dukascopy 2003-2024)
con entrenamiento caminando (reentreno anual), mide en la reserva 2019-01-01 a 2024-04-01 y en
Deriv desde 2024-04-01. Guarda los modelos finales en <carpeta_ml>/modelos.

Uso (con el entorno de D:):
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/modelo_ml.py D:/traiding_ml docs/validacion/eventos_eeuu_2003_2024.json docs/validacion/modelo_ml_2026-11-16
Los CSV de velas van en <carpeta_ml>/datos/<Plataforma>_<symbol>_1h.csv (epoch,open,high,low,close).
"""
import datetime as dt
import json
import math
import os
import sys
import time

import joblib
import lightgbm as lgb
import numpy as np
import pandas as pd
from sklearn.linear_model import LogisticRegression

MERCADOS = ['frxEURUSD', 'frxGBPJPY', 'frxGBPUSD', 'frxUSDCHF', 'frxUSDJPY', 'frxXAUUSD', 'frxXAGUSD']
HORIZONTES = {'binarias': 2, 'multiplicadores': 6}
INICIO = int(dt.datetime(2003, 5, 1, tzinfo=dt.timezone.utc).timestamp())
FIN_DUKA = int(dt.datetime(2024, 4, 1, tzinfo=dt.timezone.utc).timestamp())
ANIOS_DESARROLLO = list(range(2009, 2019))
ANIOS_RESERVA = list(range(2019, 2025))
SEPARACION = 86400
COBERTURAS = {'5%': 95, '10%': 90, '20%': 80, '100%': 0}
EQ85, EQ78 = 100 / 185, 100 / 178
Z_CRITICO = 2.24
COMISIONES = [0.0001, 0.0002, 0.0004]
SEMILLA = 42


def epoch_anio(a):
    return int(dt.datetime(a, 1, 1, tzinfo=dt.timezone.utc).timestamp())


# ------------------------------------------------------------------ variables
def cargar(carpeta, plataforma, sym):
    df = pd.read_csv(os.path.join(carpeta, 'datos', '%s_%s_1h.csv' % (plataforma, sym)),
                     names=['epoch', 'o', 'h', 'l', 'c'])
    return df.drop_duplicates('epoch').sort_values('epoch').reset_index(drop=True)


def rsi(c, n=14):
    d = c.diff()
    sube = d.clip(lower=0).ewm(alpha=1 / n, adjust=False).mean()
    baja = (-d.clip(upper=0)).ewm(alpha=1 / n, adjust=False).mean()
    return 100 - 100 / (1 + sube / baja.replace(0, np.nan))


def variables_serie(df, eventos):
    c, o, h, l = df['c'], df['o'], df['h'], df['l']
    lr = np.log(c).diff()
    vol24 = lr.rolling(24).std().replace(0, np.nan)
    vol120 = lr.rolling(120).std().replace(0, np.nan)
    f = pd.DataFrame({'epoch': df['epoch']})
    for k in [1, 2, 3, 4, 6, 12, 24, 120]:
        f['r%d' % k] = np.log(c / c.shift(k)) / (vol24 * math.sqrt(k))
    f['vol24'] = vol24
    f['vol120'] = vol120
    f['vol_razon'] = vol24 / vol120
    rango = (h - l).replace(0, np.nan)
    f['rango'] = (h - l) / c / vol24
    f['cuerpo'] = (c - o) / rango
    f['mecha_sup'] = (h - np.maximum(o, c)) / rango
    f['mecha_inf'] = (np.minimum(o, c) - l) / rango
    f['rsi14'] = rsi(c)
    f['bb20'] = (c - c.rolling(20).mean()) / c.rolling(20).std().replace(0, np.nan)
    f['ema_dif'] = (c.ewm(span=12, adjust=False).mean() - c.ewm(span=26, adjust=False).mean()) / c / vol24
    tp = (h + l + c) / 3
    sma = tp.rolling(20).mean()
    mad = tp.rolling(20).apply(lambda x: np.mean(np.abs(x - x.mean())), raw=True)
    f['cci20'] = (tp - sma) / (0.015 * mad.replace(0, np.nan))
    f['dist_max24'] = (c - h.rolling(24).max()) / c / vol24
    f['dist_min24'] = (c - l.rolling(24).min()) / c / vol24
    cierre = df['epoch'] + 3600                      # se decide al cierre de la vela
    hora = (cierre % 86400) / 3600.0
    f['hora_sin'] = np.sin(2 * np.pi * hora / 24)
    f['hora_cos'] = np.cos(2 * np.pi * hora / 24)
    f['dia'] = pd.to_datetime(cierre, unit='s', utc=True).dt.day_of_week
    ini = np.array(sorted(e[0] for e in eventos))
    fin = np.array(sorted(e[1] for e in eventos))
    cv = cierre.to_numpy()
    f['noticia_prox2h'] = (np.searchsorted(ini, cv + 7200, side='right') - np.searchsorted(ini, cv, side='right')) > 0
    f['noticia_ult2h'] = (np.searchsorted(fin, cv, side='right') - np.searchsorted(fin, cv - 7200, side='left')) > 0
    f['noticia_prox2h'] = f['noticia_prox2h'].astype(float)
    f['noticia_ult2h'] = f['noticia_ult2h'].astype(float)
    # objetivos: cierre H velas despues, solo si no hay hueco de datos
    for nombre, hz in HORIZONTES.items():
        fut = c.shift(-hz)
        ok = (df['epoch'].shift(-hz) - df['epoch'] == hz * 3600) & (fut != c) & fut.notna()
        f['ret_' + nombre] = (fut / c - 1).where(ok)
        f['y_' + nombre] = (fut > c).astype(float).where(ok)
    f['_r1'] = lr / vol24
    f['_r6'] = np.log(c / c.shift(6)) / (vol24 * math.sqrt(6))
    return f


def construir(carpeta, plataforma, eventos):
    partes = {}
    for sym in MERCADOS:
        f = variables_serie(cargar(carpeta, plataforma, sym), eventos)
        f['symbol'] = sym
        partes[sym] = f
    # retornos de los otros mercados en la misma hora
    cruz = None
    for sym, f in partes.items():
        x = f[['epoch', '_r1', '_r6']].rename(columns={'_r1': 'x_%s_r1' % sym[3:], '_r6': 'x_%s_r6' % sym[3:]})
        cruz = x if cruz is None else cruz.merge(x, on='epoch', how='outer')
    todo = []
    for sym, f in partes.items():
        g = f.drop(columns=['_r1', '_r6']).merge(cruz, on='epoch', how='left')
        for s in MERCADOS:
            g['m_' + s[3:]] = float(s == sym)
        todo.append(g)
    d = pd.concat(todo, ignore_index=True)
    d['cierre'] = d['epoch'] + 3600
    return d


def columnas_variables(d):
    return [k for k in d.columns if k not in ('epoch', 'cierre', 'symbol') and not k.startswith('y_') and not k.startswith('ret_')]


# ------------------------------------------------------------------ modelos
class Logistica:
    def __init__(self):
        self.m = LogisticRegression(C=1.0, max_iter=2000)

    def fit(self, X, y):
        self.media = X.mean()
        self.desv = X.std().replace(0, 1).fillna(1)
        self.m.fit(self._t(X), y)
        return self

    def _t(self, X):
        return ((X - self.media) / self.desv).fillna(0).clip(-10, 10).to_numpy(np.float32)

    def proba(self, X):
        return self.m.predict_proba(self._t(X))[:, 1]


class Arboles:
    def __init__(self):
        self.m = lgb.LGBMClassifier(n_estimators=300, learning_rate=0.03, num_leaves=31, min_child_samples=500,
                                    subsample=0.8, subsample_freq=1, colsample_bytree=0.8, n_jobs=8,
                                    random_state=SEMILLA, verbose=-1)

    def fit(self, X, y):
        self.m.fit(X.to_numpy(np.float32), y)
        return self

    def proba(self, X):
        return self.m.predict_proba(X.to_numpy(np.float32))[:, 1]


MODELOS = {'logistica': Logistica, 'lightgbm': Arboles}


# ------------------------------------------------------------------ evaluacion
def operar(filas, p, umbral, hz):
    """Una posicion a la vez por mercado; opera si |p-0,5| >= umbral."""
    sel = filas.assign(p=p)
    sel = sel[np.abs(sel['p'] - 0.5) >= umbral].sort_values(['symbol', 'cierre'])
    tomadas = []
    for sym, g in sel.groupby('symbol'):
        libre = -1
        for cierre, pp, ret in zip(g['cierre'].to_numpy(), g['p'].to_numpy(), g['ret'].to_numpy()):
            if cierre < libre:
                continue
            tomadas.append((cierre, 1.0 if pp > 0.5 else -1.0, ret))
            libre = cierre + hz * 3600
    if not tomadas:
        return np.zeros((0, 3))
    return np.array(tomadas)


def resumen(ops):
    n = len(ops)
    if n == 0:
        return {'operaciones': 0}
    firmado = ops[:, 1] * ops[:, 2]
    g = int((firmado > 0).sum())
    acierto = g / n
    media, desv = float(firmado.mean()), float(firmado.std(ddof=1)) if n > 1 else 0.0
    se = desv / math.sqrt(n) if n > 1 else float('nan')
    r = {'operaciones': n, 'ganadas': g, 'acierto': round(acierto, 4),
         'z_vs_85': round((acierto - EQ85) / math.sqrt(EQ85 * (1 - EQ85) / n), 3),
         'retorno_medio_pct': round(media * 100, 5),
         'z_retorno_bruto': round(media / se, 3) if se > 0 else 0.0}
    for com in COMISIONES:
        r['z_retorno_com_%g' % (com * 100)] = round((media - com) / se, 3) if se > 0 else 0.0
    return r


def entrenar(clase, d, cols, hz, nombre_y, hasta):
    """Entrena con filas cuyo horizonte termina antes de (hasta - SEPARACION)."""
    tr = d[(d['cierre'] >= INICIO) & (d['cierre'] + hz * 3600 < hasta - SEPARACION) & d[nombre_y].notna()]
    m = clase().fit(tr[cols], tr[nombre_y].to_numpy())
    p_tr = m.proba(tr[cols])
    umbrales = {k: float(np.percentile(np.abs(p_tr - 0.5), q)) if q > 0 else 0.0 for k, q in COBERTURAS.items()}
    return m, umbrales, len(tr)


def evaluar(m, umbrales, filas, cols, hz, nombre):
    p = m.proba(filas[cols])
    base = filas[['symbol', 'cierre']].assign(ret=filas['ret_' + nombre].to_numpy())
    return {k: operar(base, p, u, hz) for k, u in umbrales.items()}, p


def main(carpeta, ruta_eventos, salida):
    t0 = time.time()
    ev = json.load(open(ruta_eventos, encoding='utf-8'))['eventos']
    eventos = [(int(dt.datetime.fromisoformat(e['inicio_utc'].replace('Z', '+00:00')).timestamp()),
                int(dt.datetime.fromisoformat(e['fin_utc'].replace('Z', '+00:00')).timestamp())) for e in ev]
    duka = construir(carpeta, 'Dukascopy', eventos)
    duka = duka[duka['cierre'] < FIN_DUKA].reset_index(drop=True)
    deriv = construir(carpeta, 'Deriv', eventos)
    deriv = deriv[deriv['cierre'] >= FIN_DUKA].reset_index(drop=True)
    cols = columnas_variables(duka)
    print('filas Dukascopy', len(duka), 'Deriv', len(deriv), 'variables', len(cols), '%.0fs' % (time.time() - t0), flush=True)

    lineas = ['MODELO DE APRENDIZAJE AUTOMATICO - plan pre-registrado en docs/validacion/modelo_ml_plan.md',
              'Velas 1h, 7 mercados, %d variables. Se opera el 10%% mas seguro (umbral del entrenamiento), una posicion a la vez por mercado.' % len(cols),
              'Binarias: ventaja si reserva 1 acierto > 54,05%% con z >= %.2f, acierto > 56,18%% y reserva 2 > 54,05%%.' % Z_CRITICO,
              'Multiplicadores: ventaja si reserva 1 (retorno a favor - 0,02%%) con z >= %.2f y reserva 2 positivo.' % Z_CRITICO, '']
    salida_json = {'variables': cols, 'resultados': {}}
    os.makedirs(os.path.join(carpeta, 'modelos'), exist_ok=True)

    for nombre, hz in HORIZONTES.items():
        ny = 'y_' + nombre
        res_dur = duka[duka[ny].notna()]
        base_compra = float(res_dur.loc[res_dur['cierre'] >= epoch_anio(2019), ny].mean())
        lineas.append('== %s (cierre %d velas despues). Referencia comprar siempre en reserva 1: %.2f%% de subidas' % (nombre.upper(), hz, base_compra * 100))
        for nm, clase in MODELOS.items():
            por_periodo = {'desarrollo': {k: [] for k in COBERTURAS}, 'reserva1': {k: [] for k in COBERTURAS}}
            por_anio = []
            for anio in ANIOS_DESARROLLO + ANIOS_RESERVA:
                ini, fin = epoch_anio(anio), min(epoch_anio(anio + 1), FIN_DUKA)
                m, umb, n_tr = entrenar(clase, duka, cols, hz, ny, ini)
                filas = duka[(duka['cierre'] >= ini) & (duka['cierre'] < fin) & duka[ny].notna()]
                ops, _ = evaluar(m, umb, filas, cols, hz, nombre)
                periodo = 'desarrollo' if anio in ANIOS_DESARROLLO else 'reserva1'
                for k in COBERTURAS:
                    por_periodo[periodo][k].append(ops[k])
                r10 = resumen(ops['10%'])
                por_anio.append({'anio': anio, 'periodo': periodo, 'filas_entrenamiento': n_tr, 'umbral_10': umb['10%'], **r10})
                print('  %s %s %d: entrenamiento %d, 10%% -> %s ops acierto %s  (%.0fs)' % (
                    nombre, nm, anio, n_tr, r10.get('operaciones'), r10.get('acierto'), time.time() - t0), flush=True)
            # reserva 2: entrenado con todo Dukascopy, aplicado a Deriv
            m, umb, n_tr = entrenar(clase, duka, cols, hz, ny, FIN_DUKA + SEPARACION)
            filas2 = deriv[deriv[ny].notna()]
            ops2, _ = evaluar(m, umb, filas2, cols, hz, nombre)
            ext = 'txt' if nm == 'lightgbm' else 'joblib'
            ruta_modelo = os.path.join(carpeta, 'modelos', '%s_%s.%s' % (nm, nombre, ext))
            if nm == 'lightgbm':
                m.m.booster_.save_model(ruta_modelo)
            else:
                joblib.dump({'modelo': m.m, 'media': m.media, 'desv': m.desv, 'columnas': cols}, ruta_modelo)
            r = {'por_anio': por_anio}
            for periodo in ('desarrollo', 'reserva1'):
                r[periodo] = {k: resumen(np.vstack(v) if len(v) else np.zeros((0, 3))) for k, v in por_periodo[periodo].items()}
            r['reserva2_deriv'] = {k: resumen(v) for k, v in ops2.items()}
            r['umbrales_final'] = umb
            r1, r2 = r['reserva1']['10%'], r['reserva2_deriv']['10%']
            if nombre == 'binarias':
                ventaja = (r1.get('operaciones', 0) >= 30 and r1['z_vs_85'] >= Z_CRITICO and r1['acierto'] > EQ78
                           and r2.get('operaciones', 0) > 0 and r2['acierto'] > EQ85)
            else:
                ventaja = (r1.get('operaciones', 0) >= 30 and r1['z_retorno_com_0.02'] >= Z_CRITICO
                           and r2.get('operaciones', 0) > 0 and r2['retorno_medio_pct'] > 0)
            r['veredicto'] = 'VENTAJA' if ventaja else 'SIN VENTAJA'
            salida_json['resultados']['%s_%s' % (nombre, nm)] = r
            lineas.append('  %s => %s   (modelo guardado en %s)' % (nm, r['veredicto'], ruta_modelo))
            for periodo, titulo in (('desarrollo', 'desarrollo 2009-2018'), ('reserva1', 'reserva 1 2019-2024'), ('reserva2_deriv', 'reserva 2 Deriv')):
                for k in COBERTURAS:
                    x = r[periodo][k]
                    if not x.get('operaciones'):
                        lineas.append('    %-22s %4s: sin operaciones' % (titulo, k))
                        continue
                    lineas.append('    %-22s %4s: %6d ops  acierto %6.2f%%  z(85%%) %6.2f  retorno medio %+.4f%%  z bruto %5.2f  z com 0,02%% %6.2f' % (
                        titulo, k, x['operaciones'], x['acierto'] * 100, x['z_vs_85'], x['retorno_medio_pct'], x['z_retorno_bruto'], x['z_retorno_com_0.02']))
            lineas.append('    por ano (10%): ' + ', '.join('%d %s%%' % (a['anio'], round(a['acierto'] * 100, 1) if a.get('operaciones') else '-') for a in por_anio))
        lineas.append('')
    lineas.append('Tiempo total: %.0f min' % ((time.time() - t0) / 60))
    open(salida + '.txt', 'w', encoding='utf-8').write('\n'.join(lineas) + '\n')
    json.dump(salida_json, open(salida + '.json', 'w', encoding='utf-8'), indent=1, default=float)
    print('\n'.join(lineas))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2], sys.argv[3])
