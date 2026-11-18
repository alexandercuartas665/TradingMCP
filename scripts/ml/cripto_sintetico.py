"""Cripto: entrenar con datos sinteticos y prueba placebo (docs/validacion/cripto_sintetico_placebo_plan.md).

Partes (cada una guarda su JSON en <carpeta>/cripto/sintetico/ y se puede correr por separado):
  A   LightGBM H4 con reales (S0), reales + sinteticos 1x (S1) y 3x (S2), 5 semillas del generador
  B1  placebo del modelo: 30 mercados con signo al azar hora a hora, H4 completo
  B2  placebo de las reglas diarias: 1000 mercados con signo al azar dia a dia, H1/H2 y lote 2
  informe  une los tres JSON en docs/validacion/cripto_sintetico_<fecha>.txt/.json

Uso (entorno de D:):
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/cripto_sintetico.py D:/traiding_ml A
  ... D:/traiding_ml B1
  ... D:/traiding_ml B2
  ... D:/traiding_ml informe docs/validacion/cripto_sintetico_2026-11-18
"""
import json
import math
import os
import sys
import time
import datetime as dt
from concurrent.futures import ProcessPoolExecutor

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)

import lightgbm as lgb  # noqa: E402
import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402

import cripto_estudio as C  # noqa: E402
import cripto_lote2 as L  # noqa: E402

SEMANA = 7 * 86400
LUNES = 4 * 86400                      # 1970-01-05 fue lunes
BASE_SINT = C.ep(2000, 1, 3)           # lunes: epocas ficticias de los sinteticos
SEMILLAS_A = [1, 2, 3, 4, 5]
MULT = {'S1': 1, 'S2': 3}
N_B1 = 30
N_B2 = 1000
Z_A = 1.96
NIVEL = '10%'
TRAMOS = [(C.ep(a), C.ep(a + 1)) for a in range(2021, 2025)] + [(C.ep(2025), C.CORTE)]
COLS_H = ['o', 'h', 'l', 'c', 'volumen', 'compra_agresiva', 'operaciones']


def carpeta_sal(carpeta):
    c = os.path.join(carpeta, 'cripto', 'sintetico')
    os.makedirs(c, exist_ok=True)
    return c


def cargar_horas(carpeta):
    return {s: C.cargar(carpeta, 'Binance_%s_1h.csv' % s) for s in C.MONEDAS}


# ================================================================== generador sintetico (bootstrap de semanas)
def razones(horas):
    """Panel horario comun. Por moneda: retornos relativos al cierre anterior y volumenes normalizados."""
    g0 = min(int(h['epoch'].min()) for h in horas.values())
    g1 = max(int(h['epoch'].max()) for h in horas.values())
    grid = np.arange(g0, g1 + 3600, 3600)
    R = {}
    for s, h in horas.items():
        x = h.set_index('epoch')[COLS_H].astype(float).reindex(grid)
        cp = x['c'].shift(1)
        vm = x['volumen'].rolling(720, min_periods=240).mean().shift(1)
        om = x['operaciones'].rolling(720, min_periods=240).mean().shift(1)
        R[s] = {'rc': (x['c'] / cp).to_numpy(), 'ro': (x['o'] / cp).to_numpy(), 'rh': (x['h'] / cp).to_numpy(),
                'rl': (x['l'] / cp).to_numpy(), 'vn': (x['volumen'] / vm).to_numpy(),
                'tbn': (x['compra_agresiva'] / vm).to_numpy(), 'opn': (x['operaciones'] / om).to_numpy()}
    return grid, R


def generar(grid, R, hasta, mult, rng):
    """Semanas reales completas (lunes a lunes) anteriores a hasta - 1 dia, elegidas con reemplazo."""
    inicios = grid[((grid - LUNES) % SEMANA == 0) & (grid >= C.INICIO_ML) & (grid + SEMANA <= hasta - C.SEPARACION)]
    k = mult * len(inicios)
    elegidas = rng.choice(inicios, size=k, replace=True)
    ix = ((elegidas - grid[0]) // 3600)[:, None] + np.arange(168)[None, :]
    ix = ix.ravel()
    epoch = BASE_SINT + np.arange(len(ix)) * 3600
    horas = {}
    for s, r in R.items():
        rc, ro, rh, rl = r['rc'][ix], r['ro'][ix], r['rh'][ix], r['rl'][ix]
        ok = ~(np.isnan(rc) | np.isnan(ro) | np.isnan(rh) | np.isnan(rl))
        c = np.cumprod(np.where(ok, rc, 1.0))
        cprev = np.concatenate([[1.0], c[:-1]])
        df = pd.DataFrame({'epoch': epoch, 'o': ro * cprev, 'h': rh * cprev, 'l': rl * cprev, 'c': c,
                           'volumen': r['vn'][ix], 'compra_agresiva': r['tbn'][ix], 'operaciones': r['opn'][ix]})
        horas[s] = df[ok].reset_index(drop=True)
    return horas, len(inicios)


# ================================================================== entrenamiento y evaluacion H4
def entrenar_mix(d, cols, hasta, sint=None):
    tr = d[(d['cierre'] >= C.INICIO_ML) & (d['cierre'] + C.HZ * 3600 < hasta - C.SEPARACION) & d['y'].notna()]
    X = tr[cols].to_numpy(np.float32)
    y = tr['y'].to_numpy()
    if sint is not None and len(sint):
        s = sint[sint['y'].notna()]
        X = np.vstack([X, s[cols].to_numpy(np.float32)])
        y = np.concatenate([y, s['y'].to_numpy()])
    m = lgb.LGBMClassifier(n_estimators=300, learning_rate=0.03, num_leaves=31, min_child_samples=500, subsample=0.8,
                           subsample_freq=1, colsample_bytree=0.8, n_jobs=8, random_state=C.SEMILLA, verbose=-1)
    m.fit(X, y)
    p = m.predict_proba(tr[cols].to_numpy(np.float32))[:, 1]       # umbrales solo con reales
    umb = {k: float(np.percentile(np.abs(p - 0.5), q)) for k, q in C.COBERTURAS.items()}
    return m, umb, len(y)


def resumen(ops):
    r = C.resumen(ops)
    if len(ops) >= 2:
        b = np.array([o[2] * o[3] for o in ops])
        r['z_bruto'] = round(float(b.mean() / (b.std(ddof=1) / math.sqrt(len(b)))), 3)
    return r


def correr_h4(d, cols, sinteticos=None, variantes=('S0',)):
    """sinteticos(ini, hasta) -> {variante: DataFrame o None}. Devuelve {variante: {reserva1, reserva2}}."""
    base = d[d['y'].notna()]
    r1 = {v: [] for v in variantes}
    r2 = {v: [] for v in variantes}
    tamanos = {v: [] for v in variantes}
    for ini, fn in TRAMOS + [(C.CORTE, None)]:
        f = base[(base['cierre'] >= ini) & ((base['cierre'] < fn) if fn else True)]
        filas = f[['symbol', 'cierre', 'ret']]
        S = sinteticos(ini) if sinteticos else {}
        for v in variantes:
            m, umb, n = entrenar_mix(d, cols, ini, S.get(v))
            tamanos[v].append(n)
            p = m.predict_proba(f[cols].to_numpy(np.float32))[:, 1]
            (r1 if fn else r2)[v] += C.operar(filas, p, umb[NIVEL])
    return {v: {'reserva1': resumen(r1[v]), 'reserva2': resumen(r2[v]), 'filas_entrenamiento': tamanos[v]} for v in variantes}


def parte_a(carpeta):
    t0 = time.time()
    sal = os.path.join(carpeta_sal(carpeta), 'A.json')
    J = json.load(open(sal)) if os.path.exists(sal) else {}
    horas = cargar_horas(carpeta)
    d = C.construir(horas)
    cols = C.columnas(d)
    if 'S0' not in J:
        J['S0'] = correr_h4(d, cols)['S0']
        json.dump(J, open(sal, 'w'), indent=1, default=float)
    print('S0 %s (%.1f min)' % (J['S0'], (time.time() - t0) / 60), flush=True)
    grid, R = razones(horas)
    for semilla in SEMILLAS_A:
        clave = 'semilla_%d' % semilla
        if clave in J:
            continue
        info = {}

        def sinteticos(ini, semilla=semilla, info=info):
            rng = np.random.default_rng(semilla * 1000 + ini // 86400)
            hs, semanas = generar(grid, R, ini, MULT['S2'], rng)
            ds = C.construir(hs)
            limite = BASE_SINT + semanas * MULT['S1'] * SEMANA
            info[str(dt.datetime.utcfromtimestamp(ini).date())] = {'semanas_reales': semanas, 'filas_S2': int(ds['y'].notna().sum())}
            return {'S1': ds[ds['cierre'] < limite], 'S2': ds}

        res = correr_h4(d, cols, sinteticos, variantes=('S1', 'S2'))
        res['generador'] = info
        J[clave] = res
        json.dump(J, open(sal, 'w'), indent=1, default=float)
        print('%s S1 %s | S2 %s (%.1f min)' % (clave, res['S1']['reserva2'], res['S2']['reserva2'], (time.time() - t0) / 60), flush=True)


# ================================================================== placebo
def voltear_horas(horas, rng):
    """Signo al azar por hora (el mismo para todas las monedas): refleja la vela alrededor del cierre anterior."""
    g0 = min(int(h['epoch'].min()) for h in horas.values())
    g1 = max(int(h['epoch'].max()) for h in horas.values())
    signo = rng.choice([-1.0, 1.0], size=(g1 - g0) // 3600 + 1)
    out = {}
    for s, h in horas.items():
        h = h.reset_index(drop=True)
        sg = signo[(h['epoch'].to_numpy() - g0) // 3600][1:]
        cp = h['c'].to_numpy()[:-1]
        lo, lh, ll, lc = (np.log(h[k].to_numpy()[1:] / cp) for k in ('o', 'h', 'l', 'c'))
        lh2 = np.where(sg > 0, lh, -ll)
        ll2 = np.where(sg > 0, ll, -lh)
        c = h['c'].iloc[0] * np.exp(np.cumsum(sg * lc))
        cprev = np.concatenate([[h['c'].iloc[0]], c[:-1]])
        v = h['volumen'].to_numpy()[1:]
        tb = h['compra_agresiva'].to_numpy()[1:]
        out[s] = pd.DataFrame({'epoch': h['epoch'].to_numpy()[1:], 'o': cprev * np.exp(sg * lo), 'h': cprev * np.exp(lh2),
                               'l': cprev * np.exp(ll2), 'c': c, 'volumen': v, 'compra_agresiva': np.where(sg > 0, tb, v - tb),
                               'operaciones': h['operaciones'].to_numpy()[1:]})
    return out


def parte_b1(carpeta):
    t0 = time.time()
    sal = os.path.join(carpeta_sal(carpeta), 'B1.json')
    J = json.load(open(sal)) if os.path.exists(sal) else {}
    horas = cargar_horas(carpeta)
    for i in range(N_B1):
        clave = 'placebo_%02d' % i
        if clave in J:
            continue
        hp = voltear_horas(horas, np.random.default_rng(5000 + i))
        d = C.construir(hp)
        J[clave] = correr_h4(d, C.columnas(d))['S0']
        json.dump(J, open(sal, 'w'), indent=1, default=float)
        print('%s r1 %s | r2 %s (%.1f min)' % (clave, J[clave]['reserva1'], J[clave]['reserva2'], (time.time() - t0) / 60), flush=True)


def voltear_dias(D, rng):
    dias = sorted(set().union(*[set(d.index) for d in D.values()]))
    signo = pd.Series(rng.choice([-1.0, 1.0], size=len(dias)), index=dias)
    out = {}
    for s, d in D.items():
        sg = signo.reindex(d.index).to_numpy()
        lr = np.log(d['c'] / d['c'].shift(1)).fillna(0.0).to_numpy()
        c = d['c'].iloc[0] * np.exp(np.cumsum(sg * lr))
        out[s] = pd.DataFrame({'c': c, 'v': d['v'].to_numpy(), 'tb': np.where(sg > 0, d['tb'], d['v'] - d['tb'])}, index=d.index)
    return out


_B2 = {}


def _b2_iniciar(carpeta):
    _B2['D'] = {s: L.diarias(C.cargar(carpeta, 'Binance_%s_1h.csv' % s)) for s in C.MONEDAS}
    _B2['F'] = {s: L.funding_diario(carpeta, s) for s in C.MONEDAS}


def _b2_uno(i):
    Dp = voltear_dias(_B2['D'], np.random.default_rng(9000 + i))
    C_, r, P = L.posiciones(Dp, _B2['F'])
    fin = int(C_.index.max()) + 86400
    reserva = (C.CORTE, fin)
    out = {}
    for k in ['H1_referencia', 'E1_canal_20_10', 'E2_tendencia_vol', 'E3_filtro_btc', 'E4_canal_volumen', 'E5_rebote_panico', 'E6_financiamiento']:
        dd, bd = L.evaluar(r, P[k], C.COSTO, L.DISENO)
        rr, br = L.evaluar(r, P[k], C.COSTO, reserva)
        out[k] = {'t': dd['t_alfa'], 'alfa': dd['alfa_anual_pct'], 'alfa_res': rr['alfa_anual_pct'],
                  'caida': dd['caida_max_pct'], 'caida_res': rr['caida_max_pct'], 'sharpe': dd['sharpe']}
        out['BH'] = {'caida': bd['caida_max_pct'], 'caida_res': br['caida_max_pct'], 'sharpe': bd['sharpe']}
    reglas = C.prueba_reglas({s: d['c'] for s, d in Dp.items()}, C.COSTO, C.INICIO_PRUEBA, fin)
    for k in ('H1_tendencia', 'H2_momentum_serie'):
        out[k] = {'t': reglas[k]['t_alfa'], 'alfa': reglas[k]['alfa_anual_pct']}
    return i, out


def parte_b2(carpeta, trabajadores=6):
    t0 = time.time()
    sal = os.path.join(carpeta_sal(carpeta), 'B2.json')
    J = json.load(open(sal)) if os.path.exists(sal) else {}
    pend = [i for i in range(N_B2) if str(i) not in J]
    with ProcessPoolExecutor(trabajadores, initializer=_b2_iniciar, initargs=(carpeta,)) as ex:
        for n, (i, out) in enumerate(ex.map(_b2_uno, pend, chunksize=4)):
            J[str(i)] = out
            if n % 50 == 49:
                json.dump(J, open(sal, 'w'), default=float)
                print('B2 %d/%d (%.1f min)' % (len(J), N_B2, (time.time() - t0) / 60), flush=True)
    json.dump(J, open(sal, 'w'), default=float)
    print('B2 listo: %d placebos (%.1f min)' % (len(J), (time.time() - t0) / 60), flush=True)


# ================================================================== informe
def pct(x, q):
    return float(np.percentile(x, q))


def informe(carpeta, salida):
    c = carpeta_sal(carpeta)
    A = json.load(open(os.path.join(c, 'A.json')))
    B1 = json.load(open(os.path.join(c, 'B1.json')))
    B2 = json.load(open(os.path.join(c, 'B2.json')))
    raiz = os.path.dirname(os.path.abspath(salida))
    real1 = json.load(open(os.path.join(raiz, 'cripto_2026-11-17.json')))
    real2 = json.load(open(os.path.join(raiz, 'cripto_lote2_2026-11-17.json')))
    L_ = ['CRIPTO: DATOS SINTETICOS Y PLACEBO - plan pre-registrado en docs/validacion/cripto_sintetico_placebo_plan.md', '']
    J = {}

    # ---- A
    s0 = A['S0']
    L_.append('== PARTE A: LightGBM H4 con datos sinteticos (nivel 10%, costo 0,25% ida y vuelta)')
    L_.append('  S0 solo reales:  reserva1 %s ops acierto %.2f%% neto %+.3f%% z %.2f | reserva2 %s ops acierto %.2f%% neto %+.3f%% z %.2f' % (
        s0['reserva1']['operaciones'], s0['reserva1']['acierto'] * 100, s0['reserva1']['neto_medio_pct'], s0['reserva1']['z_neto'],
        s0['reserva2']['operaciones'], s0['reserva2']['acierto'] * 100, s0['reserva2']['neto_medio_pct'], s0['reserva2']['z_neto']))
    J['A'] = {'S0': s0}
    for v in ('S1', 'S2'):
        filas, cumple = [], 0
        for sem in SEMILLAS_A:
            x = A['semilla_%d' % sem][v]
            a, b = x['reserva1'], x['reserva2']
            ok = a['neto_medio_pct'] > 0 and a['z_neto'] >= Z_A and b['neto_medio_pct'] > 0
            cumple += ok
            filas.append(x)
            L_.append('  %s semilla %d:   reserva1 %s ops acierto %.2f%% neto %+.3f%% z %.2f | reserva2 %s ops acierto %.2f%% neto %+.3f%% z %.2f%s' % (
                v, sem, a['operaciones'], a['acierto'] * 100, a['neto_medio_pct'], a['z_neto'],
                b['operaciones'], b['acierto'] * 100, b['neto_medio_pct'], b['z_neto'], '  (cumple)' if ok else ''))
        media2 = float(np.mean([x['reserva2']['neto_medio_pct'] for x in filas]))
        ventaja = cumple >= 4
        aporta = ventaja and media2 > s0['reserva2']['neto_medio_pct']
        ver = ('VENTAJA y aporta' if aporta else 'VENTAJA, no aporta frente a S0') if ventaja else 'SIN VENTAJA'
        J['A'][v] = {'semillas': filas, 'cumplen': cumple, 'reserva2_neto_medio_promedio': media2, 'veredicto': ver}
        L_.append('  => %s (%dx sinteticos): %d de 5 semillas cumplen; reserva2 neto promedio %+.3f%% (S0 %+.3f%%) -> %s' % (
            v, MULT[v], cumple, media2, s0['reserva2']['neto_medio_pct'], ver))
    L_.append('')

    # ---- B1
    xs = [B1[k] for k in sorted(B1)]
    z1n = np.array([x['reserva1']['z_neto'] for x in xs])
    z1b = np.array([x['reserva1']['z_bruto'] for x in xs])
    z2b = np.array([x['reserva2']['z_bruto'] for x in xs])
    ac1 = np.array([x['reserva1']['acierto'] for x in xs])
    ac2 = np.array([x['reserva2']['acierto'] for x in xs])
    pasan = int(sum(1 for x in xs if x['reserva1']['neto_medio_pct'] > 0 and x['reserva1']['z_neto'] >= C.T_CRITICO
                    and x['reserva2']['neto_medio_pct'] > 0))
    z_real_b = s0['reserva1']['z_bruto']
    inflado = pasan / len(xs) > 0.05 or float(z1b.std(ddof=1)) > 1.5
    J['B1'] = {'n': len(xs), 'pasan_criterio_H4': pasan, 'z1_bruto': z1b.tolist(), 'z2_bruto': z2b.tolist(), 'z1_neto': z1n.tolist(),
               'acierto1': ac1.tolist(), 'acierto2': ac2.tolist(), 'desv_z1_bruto': float(z1b.std(ddof=1)),
               'desv_z2_bruto': float(z2b.std(ddof=1)), 'z_real_bruto_reserva1': z_real_b,
               'percentil_z_real': float((z1b < z_real_b).mean() * 100), 'inflado': inflado}
    L_.append('== PARTE B1: placebo del modelo, %d mercados con signo al azar hora a hora (H4 completo, nivel 10%%)' % len(xs))
    L_.append('  reserva 1: acierto medio %.2f%% (min %.2f%%, max %.2f%%); z bruto media %.2f desv %.2f (min %.2f, max %.2f); z neto media %.2f' % (
        ac1.mean() * 100, ac1.min() * 100, ac1.max() * 100, z1b.mean(), z1b.std(ddof=1), z1b.min(), z1b.max(), z1n.mean()))
    L_.append('  reserva 2: acierto medio %.2f%% (min %.2f%%, max %.2f%%); z bruto media %.2f desv %.2f' % (
        ac2.mean() * 100, ac2.min() * 100, ac2.max() * 100, z2b.mean(), z2b.std(ddof=1)))
    L_.append('  placebos que pasan el criterio de H4: %d de %d; con z bruto >= 1,645 en reserva 1: %d' % (
        pasan, len(xs), int((z1b >= 1.645).sum())))
    L_.append('  real (S0): acierto reserva 1 %.2f%%, z bruto %.2f -> mayor que el %.0f%% de los placebos' % (
        s0['reserva1']['acierto'] * 100, z_real_b, J['B1']['percentil_z_real']))
    L_.append('  => z del modelo %s' % ('INFLADO' if inflado else 'no inflado'))
    L_.append('')

    # ---- B2
    xs = [B2[k] for k in sorted(B2, key=int)]
    n = len(xs)
    nombres = ['E1_canal_20_10', 'E2_tendencia_vol', 'E3_filtro_btc', 'E4_canal_volumen', 'E5_rebote_panico', 'E6_financiamiento']

    def confirmada(x):
        return x['t'] >= L.T_CRITICO and x['alfa'] > 0 and x['alfa_res'] > 0

    alguna = sum(1 for x in xs if any(confirmada(x[k]) for k in nombres))
    J['B2'] = {'n': n, 'alguna_confirmada_pct': alguna / n * 100, 'estrategias': {}}
    L_.append('== PARTE B2: placebo de las reglas diarias, %d mercados con signo al azar dia a dia' % n)
    L_.append('  placebos con al menos una estrategia del lote 2 "confirmada": %d (%.1f%%)' % (alguna, alguna / n * 100))
    reales = {k: real2['estrategias'][k]['diseno']['t_alfa'] for k in ['H1_referencia'] + nombres}
    reales['H1_tendencia'] = real1['reglas']['principal']['H1_tendencia']['t_alfa']
    reales['H2_momentum_serie'] = real1['reglas']['principal']['H2_momentum_serie']['t_alfa']
    for k in ['H1_tendencia', 'H2_momentum_serie', 'H1_referencia'] + nombres:
        t = np.array([x[k]['t'] for x in xs])
        p95 = pct(t, 95)
        perc = float((t < reales[k]).mean() * 100)
        e = {'t_real': reales[k], 't_p95': p95, 't_media': float(t.mean()), 't_desv': float(t.std(ddof=1)), 'percentil_real': perc,
             'supera_p95': reales[k] > p95}
        if 'caida' in xs[0][k]:
            e['caida_mediana'] = float(np.median([x[k]['caida'] for x in xs]))
            e['caida_res_mediana'] = float(np.median([x[k]['caida_res'] for x in xs]))
            e['confirmada_pct'] = float(np.mean([confirmada(x[k]) for x in xs]) * 100) if k in nombres else None
        J['B2']['estrategias'][k] = e
        L_.append('  %-20s t real %5.2f | placebo: media %5.2f desv %4.2f p95 %5.2f -> real en percentil %5.1f%s%s' % (
            k, reales[k], e['t_media'], e['t_desv'], p95, perc, '  SUPERA p95' if e['supera_p95'] else '  no se distingue del azar',
            ('  | caida mediana diseno %.1f%% reserva %.1f%%' % (e['caida_mediana'], e['caida_res_mediana'])) if 'caida_mediana' in e else ''))
    bh = np.median([x['BH']['caida'] for x in xs]), np.median([x['BH']['caida_res'] for x in xs])
    J['B2']['BH_caida_mediana'] = [float(bh[0]), float(bh[1])]
    L_.append('  %-20s caida mediana diseno %.1f%% reserva %.1f%%' % ('comprar y mantener', bh[0], bh[1]))
    open(salida + '.txt', 'w', encoding='utf-8').write('\n'.join(L_) + '\n')
    json.dump(J, open(salida + '.json', 'w', encoding='utf-8'), indent=1, default=float)
    print('\n'.join(L_))


if __name__ == '__main__':
    carpeta, parte = sys.argv[1], sys.argv[2]
    if parte == 'A':
        parte_a(carpeta)
    elif parte == 'B1':
        parte_b1(carpeta)
    elif parte == 'B2':
        parte_b2(carpeta)
    elif parte == 'informe':
        informe(carpeta, sys.argv[3])
