"""E2 contra permutaciones: los 4 portones (plan pre-registrado docs/validacion/e2_permutacion_plan.md).

Porton 2: permutacion en diseno con optimizacion sobre 80 combinaciones de la familia de E2.
Porton 3: caminata con reoptimizacion cada 30 dias (4 anos de entrenamiento).
Porton 4: la misma caminata sobre 200 permutaciones de los datos posteriores a la primera ventana.
Porton 4b: E2 fijo en BTC con Bitstamp 2012-2016 contra 1.000 permutaciones.

Uso (entorno de D:):
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/e2_permutacion.py D:/traiding_ml docs/validacion/e2_permutacion_2026-11-21
  (--rapido: 20 permutaciones por porton, solo para probar el codigo)
"""
import datetime as dt
import json
import math
import os
import sys
import time

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)

import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402

import cripto_estudio as C  # noqa: E402
import cripto_lote2 as L2  # noqa: E402
import e2_bitstamp as EB  # noqa: E402

N_LISTA = [20, 30, 40, 50, 75, 100, 150, 200]
V_LISTA = [20, 30, 60]
OBJETIVOS = [0.3, 0.5, 0.8]
COMBOS = [(n, None, None) for n in N_LISTA] + [(n, v, o) for n in N_LISTA for v in V_LISTA for o in OBJETIVOS]
E2, H1 = (50, 30, 0.5), (50, None, None)
DISENO = (C.ep(2019), C.ep(2025, 6))
INICIO_CAMINATA = C.ep(2021, 8, 17)
ENTRENO, PASO = 4 * 365 * 86400, 30 * 86400
N_P2, N_P4, N_P4B = 1000, 200, 1000
SEMILLA = 1
RAIZ365 = math.sqrt(365)


def log(msg):
    print(time.strftime('%H:%M:%S'), msg, flush=True)


def fecha(e):
    return str(dt.datetime.fromtimestamp(int(e), dt.timezone.utc).date())


def nombre(k):
    n, v, o = k
    return 'N%d sin vol' % n if v is None else 'N%d V%d obj%.1f' % (n, v, o)


# ------------------------------------------------------------------ cartera de la familia E2
class Familia:
    """Pesos de cartera (dia x moneda) y retornos netos de las 80 combinaciones para unos retornos dados."""

    def __init__(self, r, idx):
        self.idx = idx
        self.r = r                                        # T x K con NaN donde la moneda no tiene dato
        self.disp = ~np.isnan(r)
        self.rz = np.where(self.disp, r, 0.0)
        cnt = self.disp.sum(axis=1).astype(float)
        cnt[cnt == 0] = np.nan
        empezo = np.maximum.accumulate(self.disp, axis=0)
        precio = pd.DataFrame(np.where(empezo, np.cumprod(1 + self.rz, axis=0), np.nan))
        rdf = pd.DataFrame(r)
        sig = {}
        for n in N_LISTA:
            sma = precio.rolling(n).mean()
            sig[n] = (precio > sma).astype(float).where(sma.notna()).to_numpy()
        vol = {v: rdf.rolling(v).std().to_numpy() * RAIZ365 for v in V_LISTA}
        self.bh = pd.DataFrame(r).mean(axis=1).to_numpy()   # comprar y mantener: media de las monedas con dato
        self.W, self.neto = {}, {}
        for k in COMBOS:
            n, v, o = k
            pos = sig[n] if v is None else sig[n] * np.minimum(1.0, o / vol[v])
            pos = np.vstack([np.full((1, pos.shape[1]), np.nan), pos[:-1]])     # se aplica el dia siguiente
            with np.errstate(invalid='ignore'):
                W = np.nan_to_num(np.where(self.disp, pos, np.nan) / cnt[:, None])
            self.W[k] = W
            self.neto[k] = self.neto_de(W)

    def neto_de(self, W):
        dW = np.abs(np.diff(W, axis=0, prepend=W[:1]))
        return (W * self.rz).sum(axis=1) - dW.sum(axis=1) * C.COSTO

    def mascara(self, a, b):
        return (self.idx >= a) & (self.idx < b) & ~np.isnan(self.bh)

    def t(self, y, a, b):
        m = self.mascara(a, b)
        return C.newey_west_alfa(y[m], self.bh[m])

    def caminata(self, fin):
        """Cada 30 dias elige la combinacion con mayor t en los 4 anos anteriores; pesos cosidos."""
        W = np.zeros_like(self.rz)
        elegidas = []
        d = INICIO_CAMINATA
        while d < fin:
            mejor = max(COMBOS, key=lambda k: self.t(self.neto[k], d - ENTRENO, d)[1])
            m = (self.idx >= d) & (self.idx < d + PASO)
            W[m] = self.W[mejor][m]
            elegidas.append(mejor)
            d += PASO
        return self.neto_de(W), elegidas


def permutar(r, idx, a, b, rng):
    """Baraja los retornos diarios dentro de [a, b) con la misma clave por dia para todas las monedas."""
    r = r.copy()
    pos = np.where((idx >= a) & (idx < b))[0]
    clave = rng.random(len(pos))
    for j in range(r.shape[1]):
        val = ~np.isnan(r[pos, j])
        p = pos[val]
        if len(p) > 1:
            r[p, j] = r[p[np.argsort(clave[val])], j]
    return r


def pvalor(real, dist):
    return (1 + int(np.sum(np.asarray(dist) >= real))) / (1 + len(dist))


def percentiles(dist):
    return {str(q): round(float(np.percentile(dist, q)), 3) for q in (5, 50, 95, 99)}


# ------------------------------------------------------------------ porton 4b: Bitstamp
def bitstamp(n_perm, rng):
    c = EB.serie('Bitstamp')
    r, P = EB.posiciones(c)
    real = EB.evaluar(r, P['E2'], C.COSTO, EB.DESDE, EB.HASTA)['t_alfa']
    idx = r.index.to_numpy()
    rv = r.to_numpy().reshape(-1, 1)
    dist = []
    for _ in range(n_perm):
        rp = permutar(rv, idx, EB.DESDE, EB.HASTA, rng)[:, 0]
        cp = pd.Series(float(c.iloc[0]) * np.cumprod(1 + np.nan_to_num(rp)), index=c.index)
        r2, P2 = EB.posiciones(cp)
        dist.append(EB.evaluar(r2, P2['E2'], C.COSTO, EB.DESDE, EB.HASTA)['t_alfa'])
    return real, dist


def main(carpeta, salida, rapido=False):
    t0 = time.time()
    n2, n4, n4b = (20, 20, 20) if rapido else (N_P2, N_P4, N_P4B)
    rng = np.random.default_rng(SEMILLA)
    D = {s: L2.diarias(C.cargar(carpeta, 'Binance_%s_1h.csv' % s)) for s in C.MONEDAS}
    precios = pd.DataFrame({s: d['c'] for s, d in D.items()})
    r = precios.pct_change().to_numpy()
    idx = precios.index.to_numpy()
    fin = int(idx.max()) + 86400
    J = {'plan': 'docs/validacion/e2_permutacion_plan.md', 'datos': [fecha(idx.min()), fecha(idx.max())],
         'permutaciones': {'porton2': n2, 'porton4': n4, 'porton4b': n4b}}
    L = ['E2 CONTRA PERMUTACIONES (4 portones) - plan pre-registrado en ' + J['plan'],
         'Binance 9 monedas %s a %s; 80 combinaciones; costo 0,125%% por lado; semilla %d.' % (J['datos'][0], J['datos'][1], SEMILLA), '']

    real = Familia(r, idx)
    tr = {k: real.t(real.neto[k], *DISENO)[1] for k in COMBOS}
    J['control'] = {'E2_t': round(tr[E2], 3), 'H1_t': round(tr[H1], 3)}
    L.append('== CONTROL: con datos reales E2 t %.3f (lote 2: 2,49), H1 t %.3f (lote 2: 2,64)' % (tr[E2], tr[H1]))
    if abs(tr[E2] - 2.49) > 0.01 or abs(tr[H1] - 2.64) > 0.01:
        L.append('  NO REPRODUCE el lote 2: se detiene antes de permutar (plan).')
        print('\n'.join(L))
        return 1
    mejor = max(COMBOS, key=lambda k: tr[k])
    J['diseno_real'] = {'t_max': round(tr[mejor], 3), 'mejor': nombre(mejor), 'mapa': {nombre(k): round(v, 3) for k, v in tr.items()}}
    L += ['  rejilla real en diseno: maximo t %.3f (%s)' % (tr[mejor], nombre(mejor)), '']

    # porton 2
    log('porton 2: %d permutaciones' % n2)
    dist2 = []
    for i in range(n2):
        f = Familia(permutar(r, idx, *DISENO, rng), idx)
        dist2.append(max(f.t(f.neto[k], *DISENO)[1] for k in COMBOS))
        if (i + 1) % 100 == 0:
            log('  porton 2: %d' % (i + 1))
    p2, p2e2 = pvalor(tr[mejor], dist2), pvalor(tr[E2], dist2)
    J['porton2'] = {'t_max_real': round(tr[mejor], 3), 'p': round(p2, 4), 'pasa': p2 < 0.01, 'p_e2': round(p2e2, 4),
                    'percentiles': percentiles(dist2), 'dist': [round(x, 3) for x in dist2]}
    L += ['== PORTON 2: permutacion en diseno (2019-01-01 a 2025-06-01) con optimizacion, %d permutaciones' % n2,
          '  t maximo real %.3f | permutaciones: mediana %.2f p95 %.2f p99 %.2f | p = %.4f -> %s' % (
              tr[mejor], np.median(dist2), np.percentile(dist2, 95), np.percentile(dist2, 99), p2, 'PASA' if p2 < 0.01 else 'FALLA'),
          '  informativo: E2 (t %.3f) contra los maximos de las permutaciones: p = %.4f' % (tr[E2], p2e2), '']

    # porton 3
    log('porton 3: caminata real')
    neto_c, eleg = real.caminata(fin)
    a3, t3, b3 = real.t(neto_c, INICIO_CAMINATA, fin)
    m3 = real.mascara(INICIO_CAMINATA, fin)
    e2f = real.t(real.neto[E2], INICIO_CAMINATA, fin)
    cuenta = pd.Series([nombre(k) for k in eleg]).value_counts()
    J['porton3'] = {'desde': fecha(INICIO_CAMINATA), 'hasta': fecha(fin - 86400), 'reoptimizaciones': len(eleg),
                    'alfa_anual_pct': round(a3 * 365 * 100, 2), 't': round(t3, 3), 'beta': round(b3, 3), 'pasa': a3 > 0,
                    'metricas': C.metricas(neto_c[m3]), 'comprar_mantener': C.metricas(real.bh[m3]),
                    'e2_fijo': {'alfa_anual_pct': round(e2f[0] * 365 * 100, 2), 't': round(e2f[1], 3), 'metricas': C.metricas(real.neto[E2][m3])},
                    'elegidas': cuenta.to_dict(), 'secuencia': [nombre(k) for k in eleg]}
    mc, mb, me = J['porton3']['metricas'], J['porton3']['comprar_mantener'], J['porton3']['e2_fijo']['metricas']
    L += ['== PORTON 3: caminata con reoptimizacion cada 30 dias, %s a %s (%d reoptimizaciones)' % (
              fecha(INICIO_CAMINATA), fecha(fin - 86400), len(eleg)),
          '  caminata   alfa anual %7.2f%% t %5.2f | anual %7.2f%% sharpe %5.2f caida %5.1f%% -> %s' % (
              a3 * 365 * 100, t3, mc['retorno_anual_pct'], mc['sharpe'], mc['caida_max_pct'], 'PASA' if a3 > 0 else 'FALLA'),
          '  E2 fijo    alfa anual %7.2f%% t %5.2f | anual %7.2f%% sharpe %5.2f caida %5.1f%% (informativo)' % (
              e2f[0] * 365 * 100, e2f[1], me['retorno_anual_pct'], me['sharpe'], me['caida_max_pct']),
          '  comprar y mantener                       | anual %7.2f%% sharpe %5.2f caida %5.1f%%' % (
              mb['retorno_anual_pct'], mb['sharpe'], mb['caida_max_pct']),
          '  combinaciones elegidas: ' + ', '.join('%s x%d' % (k, v) for k, v in cuenta.items()), '']

    # porton 4
    log('porton 4: %d permutaciones de la caminata' % n4)
    dist4 = []
    for i in range(n4):
        f = Familia(permutar(r, idx, INICIO_CAMINATA, fin, rng), idx)
        dist4.append(f.t(f.caminata(fin)[0], INICIO_CAMINATA, fin)[1])
        if (i + 1) % 20 == 0:
            log('  porton 4: %d' % (i + 1))
    p4 = pvalor(t3, dist4)
    J['porton4'] = {'t_real': round(t3, 3), 'p': round(p4, 4), 'pasa': p4 < 0.01, 'percentiles': percentiles(dist4),
                    'dist': [round(x, 3) for x in dist4]}
    L += ['== PORTON 4: permutacion de la caminata (datos desde %s barajados), %d permutaciones' % (fecha(INICIO_CAMINATA), n4),
          '  t real %.3f | permutaciones: mediana %.2f p95 %.2f p99 %.2f | p = %.4f -> %s' % (
              t3, np.median(dist4), np.percentile(dist4, 95), np.percentile(dist4, 99), p4, 'PASA' if p4 < 0.01 else 'FALLA'), '']

    # porton 4b
    log('porton 4b: Bitstamp, %d permutaciones' % n4b)
    t4b, dist4b = bitstamp(n4b, rng)
    p4b = pvalor(t4b, dist4b)
    J['porton4b'] = {'t_real': round(t4b, 3), 'p': round(p4b, 4), 'pasa': p4b < 0.01, 'percentiles': percentiles(dist4b),
                     'dist': [round(x, 3) for x in dist4b]}
    L += ['== PORTON 4b: E2 fijo en BTC con Bitstamp 2012-2016, %d permutaciones' % n4b,
          '  t real %.3f | permutaciones: mediana %.2f p95 %.2f p99 %.2f | p = %.4f -> %s' % (
              t4b, np.median(dist4b), np.percentile(dist4b, 95), np.percentile(dist4b, 99), p4b, 'PASA' if p4b < 0.01 else 'FALLA'), '']

    pasa = {'2': p2 < 0.01, '3': a3 > 0, '4': p4 < 0.01, '4b': p4b < 0.01}
    falla = [k for k, v in pasa.items() if not v]
    J['veredicto'] = 'PASA LOS PORTONES' if not falla else 'NO PASA (falla porton %s)' % ', '.join(falla)
    L += ['== VEREDICTO: E2 ' + J['veredicto'], '']

    L.append('== INFORMATIVO: t en diseno de las 80 combinaciones (filas N; columnas sin vol y V/objetivo)')
    cols = [(None, None)] + [(v, o) for v in V_LISTA for o in OBJETIVOS]
    L.append('  N    ' + ' '.join('%9s' % ('sin vol' if v is None else 'V%d/%.1f' % (v, o)) for v, o in cols))
    for n in N_LISTA:
        L.append('  %-4d ' % n + ' '.join('%9.2f' % tr[(n, v, o)] for v, o in cols))
    L += ['', 'Tiempo total: %.1f min' % ((time.time() - t0) / 60)]
    open(salida + '.txt', 'w', encoding='utf-8').write('\n'.join(L) + '\n')
    json.dump(J, open(salida + '.json', 'w', encoding='utf-8'), indent=1, default=float)
    print('\n'.join(L))
    return 0


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if a != '--rapido']
    sys.exit(main(args[0], args[1], rapido='--rapido' in sys.argv))
